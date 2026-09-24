// PowerShell syntax colouring for the Scripts tab's editor.
//
// The same shape as sqlEditor.js — a painted <pre> under a transparent <textarea> — for the
// reasons that file sets out: the textarea keeps the caret, selection, undo and Blazor's binding,
// and this file only ever paints. A bug in the tokeniser can make a script look wrong; it cannot
// change what runs.
//
// Two things differ, both because this holds a whole script rather than a three-line query:
//
//   * The textarea is grown to fit its content so that only the wrapper scrolls (the width-drift
//     argument in sqlEditor.js). Measuring that means setting its height to auto for an instant,
//     and on a long script that collapses the wrapper's content long enough for the browser to
//     clamp the scroll position — typing on line 200 would throw the view back to line 1 on every
//     keystroke. The scroll position is kept across the measurement.
//   * It is never shorter than the editor, so a click anywhere below the last line still lands
//     the caret at the end, as the plain textarea did. How tall the editor is depends on the pane
//     around it — the console opening, the window resizing — hence the ResizeObserver.
//
// The tokeniser is PowerShell-shaped rather than PowerShell. Where it has to guess — a bare word
// is a command at the start of a statement and an argument anywhere else — a wrong guess costs a
// hue, which is the right price for staying in step with every keystroke. PowerShell's own
// tokenizer lives in .NET, a round trip away, and colours that arrived a frame after each
// character would visibly shuffle under the caret.
window.psEditor = (function () {
    'use strict';

    // Statement keywords. Only coloured at the start of a statement (see `commandStart`), except
    // `in`, which only ever appears mid-statement — `foreach ($f in $files)`.
    var KEYWORDS = new Set([
        'begin', 'break', 'catch', 'class', 'clean', 'continue', 'data', 'do', 'dynamicparam',
        'else', 'elseif', 'end', 'enum', 'exit', 'filter', 'finally', 'for', 'foreach',
        'function', 'hidden', 'if', 'param', 'process', 'return', 'static', 'switch', 'throw',
        'trap', 'try', 'until', 'using', 'while'
    ]);

    // After these the next word is a name being defined, not a command being run.
    var DEFINES = { 'function': 'command', 'filter': 'command', 'class': 'type', 'enum': 'type' };

    // -eq, -notlike, -creplace, -isplit ... the comparison family takes a c/i prefix.
    var COMPARISON = /^[ci]?(eq|ne|gt|ge|lt|le|like|notlike|match|notmatch|replace|contains|notcontains|in|notin|split)$/;
    var OTHER_OPERATORS = new Set([
        'is', 'isnot', 'as', 'and', 'or', 'xor', 'not', 'band', 'bor', 'bxor', 'bnot', 'shl', 'shr', 'f', 'join'
    ]);

    var LITERALS = new Set(['true', 'false', 'null']);

    var NUMBER = /(0x[0-9a-f]+|\d+(\.\d+)?(e[+-]?\d+)?)(kb|mb|gb|tb|pb|ul|us|uy|[lduyns])?/iy;
    var REDIRECT = /[0-9*]>>?(&[12])?/y;

    // Shared with the SQL editor where the role is the same, so a keyword is one colour across
    // the app and dashboard.css has one rule per role rather than one per language.
    var CLASS = {
        keyword: 'sql-t-keyword',
        operator: 'ps-t-operator',
        command: 'sql-t-fn',
        parameter: 'ps-t-parameter',
        variable: 'ps-t-variable',
        literal: 'sql-t-literal',
        string: 'sql-t-string',
        escape: 'ps-t-escape',
        number: 'sql-t-number',
        type: 'sql-t-ident',
        comment: 'sql-t-comment',
        op: 'sql-t-op'
    };

    function isIdentStart(ch) { return /[A-Za-z_\u0080-￿]/.test(ch || ''); }
    function isIdentPart(ch) { return /[A-Za-z0-9_\u0080-￿]/.test(ch || ''); }
    function isSpace(ch) { return ch === ' ' || ch === '\t' || ch === '\r' || ch === '\f' || ch === '\v'; }

    // Characters after which a `#` starts a comment and a `-word` is a parameter or operator.
    // Inside a word neither is: `a#b` is one argument, and `Get-ChildItem` is one command.
    function isBoundary(ch) { return ch === undefined || /[\s(){};|&,=!]/.test(ch); }

    function escapeHtml(text) {
        return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    /// Returns [{ text, cls }] covering every character of the input exactly once.
    function tokenize(src) {
        var tokens = [];
        var pending = '';
        var i = 0;

        // True where a bare word would be the command of a new statement: at the start, after a
        // newline, ; | { } ( & or =. `npm install` colours npm and leaves install alone.
        var commandStart = true;
        var defining = null;

        // What each open [ is, so the one after a ] can be told apart: [Parameter()][string] is
        // two types in a row, $grid[0][1] two indexes.
        var brackets = [];
        var lastClosed = 'index';

        function flush() {
            if (pending) { tokens.push({ text: pending, cls: null }); pending = ''; }
        }

        function push(text, cls) {
            if (!text) return;
            flush();
            tokens.push({ text: text, cls: cls });
        }

        // An unterminated construct runs to the end of the input rather than bailing out, for
        // the sqlEditor.js reason: every string is unterminated the moment its quote is typed,
        // and the alternative is the rest of the script flickering uncoloured while you type in it.
        function indexOrEnd(needle, from) {
            var at = src.indexOf(needle, from);
            return at === -1 ? src.length : at;
        }

        // The body of a "..." string or an @"..."@ here-string: plain runs in the string colour,
        // with `escapes, $variables and $(subexpressions) picked out. A subexpression is code, so
        // its inside is tokenised as a script of its own.
        function expandable(from, to) {
            var run = from;

            function flushRun(at) { push(src.slice(run, at), CLASS.string); }

            var j = from;
            while (j < to) {
                var ch = src[j];

                if (ch === '`' && j + 1 < to) {
                    flushRun(j);
                    push(src.slice(j, j + 2), CLASS.escape);
                    j += 2; run = j;
                    continue;
                }

                if (ch === '$' && src[j + 1] === '(') {
                    flushRun(j);
                    var close = matchParen(j + 1, to);
                    push('$(', CLASS.variable);
                    var inner = tokenize(src.slice(j + 2, close));
                    for (var k = 0; k < inner.length; k++) { flush(); tokens.push(inner[k]); }
                    if (close < to) push(')', CLASS.variable);
                    j = Math.min(close + 1, to); run = j;
                    continue;
                }

                if (ch === '$') {
                    var end = variableEnd(j, to);
                    if (end > j + 1) {
                        flushRun(j);
                        push(src.slice(j, end), variableClass(src.slice(j, end)));
                        j = end; run = j;
                        continue;
                    }
                }

                j++;
            }

            flushRun(to);
        }

        // Where the ( at `open` closes, skipping over strings inside it. Approximate by design:
        // good enough to colour "Found $($dirs.Count) folders", which is what scripts contain.
        function matchParen(open, limit) {
            var depth = 0;
            for (var j = open; j < limit; j++) {
                var ch = src[j];
                if (ch === "'") { j = singleEnd(j) - 1; continue; }
                if (ch === '(') depth++;
                else if (ch === ')' && --depth === 0) return j;
            }
            return limit;
        }

        function singleEnd(start) {
            var j = start + 1;
            while (j < src.length) {
                if (src[j] === "'") {
                    if (src[j + 1] === "'") { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return src.length;
        }

        // Where a "..." string ends, and whether it was closed at all — which cannot be read back
        // off the last character, since "...`" ends in a quote and is still open.
        function doubleEnd(start) {
            var j = start + 1;
            while (j < src.length) {
                if (src[j] === '`') { j += 2; continue; }
                if (src[j] === '"') {
                    if (src[j + 1] === '"') { j += 2; continue; }
                    return { end: j + 1, closed: true };
                }
                j++;
            }
            return { end: src.length, closed: false };
        }

        // $name, $scope:name, ${any name}, and the automatic one-character ones.
        function variableEnd(start, limit) {
            var j = start + 1;
            if (src[j] === '{') return Math.min(indexOrEnd('}', j) + 1, limit);
            if (j < limit && /[_?^$]/.test(src[j]) && !isIdentPart(src[j + 1])) return j + 1;
            while (j < limit && isIdentPart(src[j])) j++;
            // One scope or drive qualifier: $env:PATH, $script:count. Not "$dir: ..." in a message.
            if (j < limit && src[j] === ':' && isIdentStart(src[j + 1])) {
                j++;
                while (j < limit && isIdentPart(src[j])) j++;
            }
            return j;
        }

        function variableClass(text) {
            return LITERALS.has(text.slice(1).toLowerCase()) ? CLASS.literal : CLASS.variable;
        }

        function prevChar() { return i > 0 ? src[i - 1] : undefined; }

        function nextNonSpace(from) {
            var j = from;
            while (j < src.length && isSpace(src[j])) j++;
            return src[j];
        }

        while (i < src.length) {
            var ch = src[i];

            if (ch === '\n') {
                pending += ch; i++;
                commandStart = true;
                continue;
            }

            if (isSpace(ch)) { pending += ch; i++; continue; }

            // <# block comment #>, which is also where comment-based help lives.
            if (ch === '<' && src[i + 1] === '#') {
                var blockEnd = src.indexOf('#>', i + 2);
                var stop = blockEnd === -1 ? src.length : blockEnd + 2;
                push(src.slice(i, stop), CLASS.comment);
                i = stop;
                continue;
            }

            if (ch === '#' && isBoundary(prevChar())) {
                var eol = indexOrEnd('\n', i);
                push(src.slice(i, eol), CLASS.comment);
                i = eol;
                continue;
            }

            // Here-strings: @" or @' ending its line, closed by "@ or '@ at the start of one.
            if (ch === '@' && (src[i + 1] === '"' || src[i + 1] === "'")) {
                var quote = src[i + 1];
                var lineEnd = indexOrEnd('\n', i + 2);
                if (/^[ \t\r]*$/.test(src.slice(i + 2, lineEnd))) {
                    var closer = src.indexOf('\n' + quote + '@', lineEnd);
                    var bodyEnd = closer === -1 ? src.length : closer + 1;
                    var hereEnd = closer === -1 ? src.length : closer + 3;
                    push(src.slice(i, lineEnd), CLASS.string);
                    if (quote === '"') expandable(lineEnd, bodyEnd);
                    else push(src.slice(lineEnd, bodyEnd), CLASS.string);
                    push(src.slice(bodyEnd, hereEnd), CLASS.string);
                    i = hereEnd;
                    commandStart = false;
                    continue;
                }
            }

            if (ch === "'") {
                var sEnd = singleEnd(i);
                push(src.slice(i, sEnd), CLASS.string);
                i = sEnd;
                commandStart = false;
                continue;
            }

            if (ch === '"') {
                var dq = doubleEnd(i);
                push('"', CLASS.string);
                expandable(i + 1, dq.closed ? dq.end - 1 : dq.end);
                if (dq.closed) push('"', CLASS.string);
                i = dq.end;
                commandStart = false;
                continue;
            }

            if (ch === '$') {
                var vEnd = variableEnd(i, src.length);
                if (vEnd > i + 1) {
                    var name = src.slice(i, vEnd);
                    push(name, variableClass(name));
                    i = vEnd;
                    commandStart = false;
                    continue;
                }
                // $( — the subexpression's own ( is handled on the next pass.
                push('$', CLASS.variable);
                i++;
                continue;
            }

            // @splat. @( and @{ are the array and hashtable openers, punctuation like ( and {.
            if (ch === '@' && isIdentStart(src[i + 1])) {
                var splatEnd = i + 1;
                while (splatEnd < src.length && isIdentPart(src[splatEnd])) splatEnd++;
                push(src.slice(i, splatEnd), CLASS.variable);
                i = splatEnd;
                commandStart = false;
                continue;
            }

            // Outside a string a backtick escapes the next character — at the end of a line, the
            // newline, which is how a long command continues onto the next one without ending.
            if (ch === '`') {
                push(src.slice(i, i + 2), CLASS.escape);
                i += 2;
                continue;
            }

            // [type] and [Attribute(...)]. Indexing — $list[0], (Get-Item)[0] — follows the thing
            // it indexes with nothing in between, which is what tells the two apart.
            if (ch === '[') {
                var before = prevChar();
                var indexing = before !== undefined &&
                    (isIdentPart(before) || before === ')' || (before === ']' && lastClosed === 'index'));
                brackets.push(indexing ? 'index' : 'type');
                pending += '['; i++;
                if (!indexing && isIdentStart(src[i])) {
                    var tEnd = i;
                    while (tEnd < src.length && (isIdentPart(src[tEnd]) || src[tEnd] === '.')) tEnd++;
                    push(src.slice(i, tEnd), CLASS.type);
                    i = tEnd;
                }
                commandStart = false;
                continue;
            }

            // Redirections — 2>$null, 2>&1, *>>log.txt. The stream number is part of the
            // operator, not a number that happens to sit in front of one.
            if (/[0-9*]/.test(ch) && src[i + 1] === '>' && isBoundary(prevChar())) {
                REDIRECT.lastIndex = i;
                var r = REDIRECT.exec(src);
                push(r[0], CLASS.op);
                i += r[0].length;
                commandStart = false;
                continue;
            }

            // -Parameter, -operator, or a negative number.
            if (ch === '-' && isBoundary(prevChar())) {
                if (/[0-9]/.test(src[i + 1] || '')) {
                    NUMBER.lastIndex = i + 1;
                    var neg = NUMBER.exec(src);
                    push('-' + neg[0], CLASS.number);
                    i += 1 + neg[0].length;
                    commandStart = false;
                    continue;
                }
                if (isIdentStart(src[i + 1])) {
                    var wEnd = i + 1;
                    while (wEnd < src.length && isIdentPart(src[wEnd])) wEnd++;
                    var bare = src.slice(i + 1, wEnd).toLowerCase();
                    var isOperator = COMPARISON.test(bare) || OTHER_OPERATORS.has(bare);
                    // -Force:$false carries its colon with it.
                    if (!isOperator && src[wEnd] === ':') wEnd++;
                    push(src.slice(i, wEnd), isOperator ? CLASS.operator : CLASS.parameter);
                    i = wEnd;
                    commandStart = false;
                    continue;
                }
            }

            // Numbers, where a word could not have begun: 42, 1.5, 0x1F, 10MB, and each end of 1..10.
            if (/[0-9]/.test(ch) && !isIdentPart(prevChar())) {
                // Sticky, so the match starts at i without slicing the rest of the script off for
                // every digit in it.
                NUMBER.lastIndex = i;
                var m = NUMBER.exec(src);
                if (m && !isIdentPart(src[i + m[0].length])) {
                    push(m[0], CLASS.number);
                    i += m[0].length;
                    commandStart = false;
                    continue;
                }
            }

            // .Member and ::Member. A method call reads as a call; a property as plain text.
            if ((ch === '.' || (ch === ':' && src[i + 1] === ':')) && isIdentStart(src[i + (ch === '.' ? 1 : 2)])) {
                var before2 = prevChar();
                var sep = ch === '.' ? 1 : 2;
                if (sep === 2 || (before2 !== undefined && (isIdentPart(before2) || before2 === ')' || before2 === ']' || before2 === '}'))) {
                    if (sep === 2) push('::', CLASS.op);
                    else pending += '.';
                    var mEnd = i + sep;
                    while (mEnd < src.length && isIdentPart(src[mEnd])) mEnd++;
                    var member = src.slice(i + sep, mEnd);
                    if (nextNonSpace(mEnd) === '(') push(member, CLASS.command);
                    else pending += member;
                    i = mEnd;
                    commandStart = false;
                    continue;
                }
            }

            if (isIdentStart(ch)) {
                // Verb-Noun and cmd.exe are one word: a hyphen or dot inside a word, followed by a
                // letter, belongs to it.
                var end = i + 1;
                while (end < src.length) {
                    if (isIdentPart(src[end])) { end++; continue; }
                    if ((src[end] === '-' || src[end] === '.') && isIdentStart(src[end + 1])) { end += 2; continue; }
                    break;
                }

                var word = src.slice(i, end);
                var lower = word.toLowerCase();
                var cls = null;

                if (defining) {
                    cls = CLASS[defining];
                    defining = null;
                    commandStart = false;
                } else if ((commandStart && KEYWORDS.has(lower)) || lower === 'in') {
                    cls = CLASS.keyword;
                    defining = DEFINES[lower] || null;
                    // The keyword begins a statement of its own: return Get-Item, else { ... }.
                    commandStart = !defining;
                } else if (commandStart && nextNonSpace(end) !== '=') {
                    // A word followed by = is being assigned to — Mandatory=$true, a hashtable
                    // key — not run.
                    cls = CLASS.command;
                    commandStart = false;
                } else {
                    commandStart = false;
                }

                if (cls) push(word, cls);
                else pending += word;
                i = end;
                continue;
            }

            if (/[=+*/%!|><&-]/.test(ch)) {
                push(ch, CLASS.op);
                i++;
                if (ch === '=' || ch === '|' || ch === '&') commandStart = true;
                continue;
            }

            if (ch === ';' || ch === '{' || ch === '}' || ch === '(') commandStart = true;
            else if (ch === ')' || ch === ']' || ch === ',') commandStart = false;
            if (ch === ']') lastClosed = brackets.pop() || 'index';

            pending += ch;
            i++;
        }

        flush();
        return tokens;
    }

    function highlight(src) {
        var html = '';
        var tokens = tokenize(src);
        for (var t = 0; t < tokens.length; t++) {
            var token = tokens[t];
            var text = escapeHtml(token.text);
            html += token.cls ? '<span class="' + token.cls + '">' + text + '</span>' : text;
        }

        // A trailing newline is not rendered, so the painted layer would be a line short.
        if (src.endsWith('\n')) html += '\n';
        return html;
    }

    var attached = new Map();

    function paint(entry) {
        entry.ink.innerHTML = highlight(entry.input.value);
        fit(entry);
    }

    function fit(entry) {
        var input = entry.input;
        var box = entry.box;

        // Not while folded away. A hidden editor measures as nothing, and fitting to nothing left
        // the textarea 0px tall — so unfolding showed the colours with no text box over them
        // until something happened to repaint. Skipped, it keeps the height it had, which is
        // still the right one the moment it is shown.
        if (box.clientWidth === 0 && box.clientHeight === 0) return;

        var keep = box.scrollTop;

        input.style.height = 'auto';
        input.style.height = Math.max(input.scrollHeight, box.clientHeight) + 'px';
        box.scrollTop = keep;

        // Nothing for the textarea itself to scroll — it is as tall as its content — but a
        // keystroke that adds a line arrives before this runs, and the browser scrolls the
        // textarea by that line to keep the caret in sight. Left there, every character would
        // sit a line above its colour.
        input.scrollTop = 0;
    }

    // Undoing that scroll also undoes what it was for: Enter on the last visible line left the
    // caret below the bottom of the editor. So the wrapper is scrolled instead, to where the
    // caret is — found in the paint layer, which holds the same characters in the same places,
    // since a textarea will not say where its caret is drawn.
    function revealCaret(entry) {
        var box = entry.box;
        var rect = caretRect(entry);

        if (!rect) {
            // A collapsed range at the start of an empty last line can have no box at all. That
            // is only ever the end of the script, so the end is where to look.
            if (entry.input.selectionEnd >= entry.input.value.length) box.scrollTop = box.scrollHeight;
            return;
        }

        var view = box.getBoundingClientRect();
        var margin = parseFloat(getComputedStyle(entry.input).lineHeight) || 20;

        if (rect.bottom > view.bottom - margin / 2) box.scrollTop += rect.bottom - view.bottom + margin / 2;
        else if (rect.top < view.top) box.scrollTop -= view.top - rect.top + margin / 2;
    }

    function caretRect(entry) {
        var offset = entry.input.selectionEnd;
        var walker = document.createTreeWalker(entry.ink, NodeFilter.SHOW_TEXT);
        var seen = 0;
        var node;

        while ((node = walker.nextNode())) {
            if (seen + node.length >= offset) {
                var range = document.createRange();
                range.setStart(node, offset - seen);
                range.collapse(true);
                var rect = range.getClientRects()[0] || range.getBoundingClientRect();
                return rect && rect.height ? rect : null;
            }
            seen += node.length;
        }
        return null;
    }

    return {
        /// Wires the pair up and paints once. Safe to call again for the same id. `dotnet`, when
        /// given, is told once per stretch of typing that the text has changed (NotifyEdited).
        attach: function (inputId, inkId, dotnet) {
            var input = document.getElementById(inputId);
            var ink = document.getElementById(inkId);
            if (!input || !ink) return;

            var existing = attached.get(inputId);
            if (existing) {
                if (existing.input === input && existing.ink === ink) {
                    existing.dotnet = dotnet || null;
                    paint(existing);
                    return;
                }
                this.detach(inputId);
            }

            var entry = { input: input, ink: ink, box: input.parentElement, dotnet: dotnet || null, told: false };

            // Only typing moves the view. A repaint from C# or a resize must leave it where it is.
            //
            // And only the first keystroke of a stretch reaches .NET. The page wants to know that
            // the script has changed — to show Save — not what it now says, which it is handed on
            // change; one call per character would re-render the whole tab per character.
            entry.onInput = function () {
                paint(entry);
                revealCaret(entry);
                if (!entry.told && entry.dotnet) {
                    entry.told = true;
                    entry.dotnet.invokeMethodAsync('NotifyEdited').catch(function () { });
                }
            };
            // The value has just gone to .NET, so the next keystroke is a new stretch.
            entry.onChange = function () { entry.told = false; };

            // A browser only fires change on blur if the text differs from when the editor took
            // focus — so type a character and delete it, and .NET, told there was an edit, never
            // hears that there is not one any more. Save stayed up with nothing to save. Blur
            // follows change, so if change has not run by now it is not coming: send it.
            entry.onBlur = function () {
                if (entry.told) input.dispatchEvent(new Event('change', { bubbles: true }));
            };
            entry.resize = new ResizeObserver(function () { fit(entry); });

            input.addEventListener('input', entry.onInput);
            input.addEventListener('change', entry.onChange);
            input.addEventListener('blur', entry.onBlur);
            entry.resize.observe(entry.box);
            attached.set(inputId, entry);
            paint(entry);

            // Only now does the textarea's own text go transparent (dashboard.css keys it off
            // this class). Until the paint layer exists it is a plain, visible editor, so a
            // script that failed to load leaves an uncoloured editor, not an invisible one.
            entry.box.classList.add('is-painted');
        },

        /// Repaint after the value changed from C# — loading another script fires no input event.
        refresh: function (inputId) {
            var entry = attached.get(inputId);
            if (!entry || !document.body.contains(entry.input)) return;
            entry.told = false;
            paint(entry);
        },

        detach: function (inputId) {
            var entry = attached.get(inputId);
            if (!entry) return;
            entry.input.removeEventListener('input', entry.onInput);
            entry.input.removeEventListener('change', entry.onChange);
            entry.input.removeEventListener('blur', entry.onBlur);
            entry.resize.disconnect();
            entry.box.classList.remove('is-painted');
            attached.delete(inputId);
        },

        // For the browser console and for checking the tokeniser by hand; the app never calls these.
        _highlight: highlight,
        _tokenize: tokenize
    };
})();
