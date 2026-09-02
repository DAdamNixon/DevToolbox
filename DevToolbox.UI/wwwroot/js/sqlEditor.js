// SQL syntax colouring for the Log Viewer's advanced query box.
//
// The technique is a highlight layer *under* a transparent <textarea>, not a
// contenteditable: the textarea keeps the caret, the selection, undo, spellcheck-off,
// IME and Blazor's own two-way binding, and this file only ever paints. Nothing here
// reads or writes the value except to render it, so a bug in the tokeniser can make the
// query look wrong but can never change what runs.
//
// The two elements have to agree on every metric that affects wrapping — font, size,
// letter spacing, padding, border, white-space — or the colours drift a character further
// out of place on each wrapped line. dashboard.css sets both from the same rules for
// exactly that reason; change one there and change the other.
//
// WIDTH is part of that, and it is the one a stylesheet cannot fix on its own: a native
// scrollbar on the textarea would narrow its content box by ~8px while the paint layer
// keeps its full width, and the two would wrap at different columns. So the textarea never
// scrolls — it is grown to fit its content here, and the wrapper does the scrolling for
// both layers at once. That is also why there is no scrollTop syncing any more.
//
// No highlighting library. Everything in wwwroot/ is vendored by hand and has to be
// registered and pinned (see the vault's 08-Third-Party), and this is a three-line query
// box rather than an IDE — CodeMirror would be two orders of magnitude more code than the
// tokeniser below.
window.sqlEditor = (function () {
    'use strict';

    // SQLite's keyword list, plus the handful of clause words that are contextual in the
    // grammar but read as keywords to a person (they are only ever coloured, so a false
    // positive costs nothing but a hue).
    var KEYWORDS = new Set([
        'abort', 'action', 'add', 'after', 'all', 'alter', 'always', 'analyze', 'and', 'as',
        'asc', 'attach', 'autoincrement', 'before', 'begin', 'between', 'by', 'cascade',
        'case', 'cast', 'check', 'collate', 'column', 'commit', 'conflict', 'constraint',
        'create', 'cross', 'current', 'current_date', 'current_time', 'current_timestamp',
        'database', 'default', 'deferrable', 'deferred', 'delete', 'desc', 'detach',
        'distinct', 'do', 'drop', 'each', 'else', 'end', 'escape', 'except', 'exclude',
        'exclusive', 'exists', 'explain', 'fail', 'filter', 'first', 'following', 'for',
        'foreign', 'from', 'full', 'generated', 'glob', 'group', 'groups', 'having', 'if',
        'ignore', 'immediate', 'in', 'index', 'indexed', 'initially', 'inner', 'insert',
        'instead', 'intersect', 'into', 'is', 'isnull', 'join', 'key', 'last', 'left',
        'like', 'limit', 'match', 'materialized', 'natural', 'no', 'not', 'nothing',
        'notnull', 'null', 'nulls', 'of', 'offset', 'on', 'or', 'order', 'others', 'outer',
        'over', 'partition', 'plan', 'pragma', 'preceding', 'primary', 'query', 'raise',
        'range', 'recursive', 'references', 'regexp', 'reindex', 'release', 'rename',
        'replace', 'restrict', 'returning', 'right', 'rollback', 'row', 'rows', 'savepoint',
        'select', 'set', 'table', 'temp', 'temporary', 'then', 'ties', 'to', 'transaction',
        'trigger', 'unbounded', 'union', 'unique', 'update', 'using', 'vacuum', 'values',
        'view', 'virtual', 'when', 'where', 'window', 'with', 'without'
    ]);

    // Literals get their own colour rather than the keyword one: in a WHERE clause the
    // difference between a keyword and a value is the thing you are looking for.
    var LITERALS = new Set(['true', 'false', 'null', 'current_date', 'current_time', 'current_timestamp']);

    // The built-ins that turn up in a log query. A name not in here still colours as a
    // function when it is followed by "(", so this list only has to cover the ones worth
    // recognising on sight; it is not a completeness claim about SQLite's surface.
    var FUNCTIONS = new Set([
        'abs', 'avg', 'cast', 'char', 'coalesce', 'count', 'date', 'datetime', 'group_concat',
        'hex', 'ifnull', 'iif', 'instr', 'json_extract', 'julianday', 'length', 'lower',
        'ltrim', 'max', 'min', 'nullif', 'printf', 'quote', 'random', 'replace', 'round',
        'rtrim', 'strftime', 'substr', 'substring', 'sum', 'time', 'total', 'trim', 'typeof',
        'unicode', 'upper', 'row_number', 'rank', 'dense_rank', 'lag', 'lead',
        'first_value', 'last_value', 'ntile'
    ]);

    var CLASS = {
        keyword: 'sql-t-keyword',
        literal: 'sql-t-literal',
        fn: 'sql-t-fn',
        string: 'sql-t-string',
        ident: 'sql-t-ident',
        number: 'sql-t-number',
        comment: 'sql-t-comment',
        param: 'sql-t-param',
        op: 'sql-t-op'
    };

    var attached = new Map();

    function escapeHtml(text) {
        return text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    // Anything above U+007F is an identifier character, which is what SQLite does. Written
    // as an explicit escape rather than a literal so the range is visible in the source.
    function isIdentStart(ch) {
        return /[A-Za-z_\u0080-\uFFFF]/.test(ch);
    }

    function isIdentPart(ch) {
        return /[A-Za-z0-9_$\u0080-\uFFFF]/.test(ch);
    }

    // Scans to the closing delimiter, honouring SQL's doubled-delimiter escape ('' inside
    // '…'). An unterminated run — which is every string the moment you type its opening
    // quote — deliberately colours to the end of the input rather than bailing out: the
    // alternative is the rest of the query flickering uncoloured while you type inside it.
    function readDelimited(sql, start, close) {
        var i = start + 1;
        while (i < sql.length) {
            if (sql[i] === close) {
                if (sql[i + 1] === close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.length;
    }

    /// Returns [{ text, cls }] covering every character of the input exactly once.
    function tokenize(sql) {
        var tokens = [];
        var i = 0;
        var pending = '';

        function flush() {
            if (pending) { tokens.push({ text: pending, cls: null }); pending = ''; }
        }

        function push(text, cls) {
            flush();
            tokens.push({ text: text, cls: cls });
        }

        while (i < sql.length) {
            var ch = sql[i];

            // -- line comment
            if (ch === '-' && sql[i + 1] === '-') {
                var eol = sql.indexOf('\n', i);
                if (eol === -1) eol = sql.length;
                push(sql.slice(i, eol), CLASS.comment);
                i = eol;
                continue;
            }

            // /* block comment */ — unterminated runs to the end, same reasoning as strings.
            if (ch === '/' && sql[i + 1] === '*') {
                var close = sql.indexOf('*/', i + 2);
                var stop = close === -1 ? sql.length : close + 2;
                push(sql.slice(i, stop), CLASS.comment);
                i = stop;
                continue;
            }

            if (ch === "'") {
                var strEnd = readDelimited(sql, i, "'");
                push(sql.slice(i, strEnd), CLASS.string);
                i = strEnd;
                continue;
            }

            // The three quoted-identifier forms SQLite accepts. Coloured apart from strings
            // because "Message" and 'Message' mean entirely different things and the whole
            // point of colouring is to make that visible before the query runs.
            if (ch === '"' || ch === '`' || ch === '[') {
                var closeCh = ch === '[' ? ']' : ch;
                var identEnd = readDelimited(sql, i, closeCh);
                push(sql.slice(i, identEnd), CLASS.ident);
                i = identEnd;
                continue;
            }

            // :name / @name / $name / ? / ?1 — parameters. The Log Viewer binds user text as
            // parameters everywhere else, so seeing one here is worth a distinct colour.
            if (ch === ':' || ch === '@' || ch === '$' || ch === '?') {
                var p = i + 1;
                while (p < sql.length && isIdentPart(sql[p])) p++;
                if (p > i + 1 || ch === '?') {
                    push(sql.slice(i, p), CLASS.param);
                    i = p;
                    continue;
                }
            }

            // Numbers, including 0x…, 1.5, 1e-3 and a leading-dot .5
            if (/[0-9]/.test(ch) || (ch === '.' && /[0-9]/.test(sql[i + 1] || ''))) {
                var n = i;
                if (ch === '0' && /[xX]/.test(sql[i + 1] || '')) {
                    n = i + 2;
                    while (n < sql.length && /[0-9a-fA-F]/.test(sql[n])) n++;
                } else {
                    while (n < sql.length && /[0-9.]/.test(sql[n])) n++;
                    if (/[eE]/.test(sql[n] || '')) {
                        n++;
                        if (/[+-]/.test(sql[n] || '')) n++;
                        while (n < sql.length && /[0-9]/.test(sql[n])) n++;
                    }
                }
                push(sql.slice(i, n), CLASS.number);
                i = n;
                continue;
            }

            if (isIdentStart(ch)) {
                var w = i;
                while (w < sql.length && isIdentPart(sql[w])) w++;
                var word = sql.slice(i, w);
                var lower = word.toLowerCase();

                // "(" after the word makes it a call, whatever it is called — which is what
                // catches the functions this list does not know about.
                var after = w;
                while (after < sql.length && /\s/.test(sql[after])) after++;
                var called = sql[after] === '(';

                var cls = null;
                if (LITERALS.has(lower)) cls = CLASS.literal;
                else if (FUNCTIONS.has(lower) && called) cls = CLASS.fn;
                else if (KEYWORDS.has(lower)) cls = CLASS.keyword;
                else if (called) cls = CLASS.fn;

                if (cls) { push(word, cls); }
                else { pending += word; }
                i = w;
                continue;
            }

            if (/[-+*/%<>=!|&~^]/.test(ch)) {
                push(ch, CLASS.op);
                i++;
                continue;
            }

            pending += ch;
            i++;
        }

        flush();
        return tokens;
    }

    function highlight(sql) {
        var html = '';
        var tokens = tokenize(sql);
        for (var t = 0; t < tokens.length; t++) {
            var token = tokens[t];
            var text = escapeHtml(token.text);
            html += token.cls ? '<span class="' + token.cls + '">' + text + '</span>' : text;
        }

        // A trailing newline is not rendered by the browser, so the highlight layer would be
        // one line shorter than the textarea and stop scrolling in step with it near the end.
        if (sql.endsWith('\n')) html += '\n';
        return html;
    }

    function paint(entry) {
        entry.ink.innerHTML = highlight(entry.input.value);
        grow(entry);
    }

    // Height reset to auto first so the element can shrink again: scrollHeight only ever
    // reports the taller of the content and the current height, so growing from the
    // existing height is a one-way ratchet that never comes back down after a delete.
    // With height:auto the textarea falls back to its `rows`, which is what keeps rows
    // working as a minimum.
    function grow(entry) {
        entry.input.style.height = 'auto';
        entry.input.style.height = entry.input.scrollHeight + 'px';
    }

    return {
        /// Wires the pair up and paints once. Safe to call again for the same id — a
        /// re-render that reuses the element must not stack a second set of listeners.
        attach: function (inputId, inkId) {
            var input = document.getElementById(inputId);
            var ink = document.getElementById(inkId);
            if (!input || !ink) return;

            var existing = attached.get(inputId);
            if (existing) {
                if (existing.input === input && existing.ink === ink) { paint(existing); return; }
                this.detach(inputId);
            }

            var entry = { input: input, ink: ink };
            entry.onInput = function () { paint(entry); };

            input.addEventListener('input', entry.onInput);
            attached.set(inputId, entry);
            paint(entry);
        },

        /// Repaint after something other than typing changed the value — loading a saved
        /// query sets it from C#, which fires no input event.
        refresh: function (inputId) {
            var entry = attached.get(inputId);
            if (entry && document.body.contains(entry.input)) paint(entry);
        },

        detach: function (inputId) {
            var entry = attached.get(inputId);
            if (!entry) return;
            entry.input.removeEventListener('input', entry.onInput);
            attached.delete(inputId);
        },

        // Exposed for the browser console and for anyone testing the tokeniser by hand;
        // nothing in the app calls it.
        _highlight: highlight
    };
})();
