using System.Collections.Generic;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The rule behind the Log Viewer's location-driven template switch: every selected location has to
/// agree on a default, and a location with none counts as disagreeing rather than abstaining. Names
/// here are <c>Alpha</c>/<c>Beta</c>, never an EE template, because the rule does not care what a
/// template is called.
/// </summary>
public class DefaultTemplateResolverTests
{
    private static readonly string[] KnownTemplates = { "Alpha", "Beta" };

    private static LogLocation Loc(string? defaultTemplate = null) => new()
    {
        Name = "L",
        Path = @"C:\Logs",
        DefaultTemplate = defaultTemplate
    };

    [Fact]
    public void An_empty_selection_has_nothing_to_resolve()
    {
        var result = DefaultTemplateResolver.Resolve(new List<LogLocation>(), KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void A_single_location_with_a_default_resolves_to_it()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Alpha") }, KnownTemplates);

        Assert.Equal("Alpha", result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void A_single_location_with_no_default_resolves_to_nothing_and_no_hint()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc() }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void Two_locations_agreeing_resolve_to_the_shared_default()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Alpha"), Loc("Alpha") }, KnownTemplates);

        Assert.Equal("Alpha", result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void Agreement_survives_case_and_whitespace_and_returns_the_canonical_name()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc(" alpha "), Loc("ALPHA") }, KnownTemplates);

        Assert.Equal("Alpha", result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void Two_locations_naming_different_templates_disagree()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Alpha"), Loc("Beta") }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.Differ, result.Hint);
    }

    [Fact]
    public void One_location_with_a_default_and_one_without_disagree_the_strict_reading()
    {
        // The strict reading named in the plan: a location with no default counts as disagreeing,
        // not as abstaining, so this is Differ rather than resolving to Alpha.
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Alpha"), Loc() }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.Differ, result.Hint);
    }

    [Fact]
    public void Every_location_with_no_default_resolves_to_nothing_and_no_hint()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc(), Loc() }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.None, result.Hint);
    }

    [Fact]
    public void Agreement_on_an_unknown_template_name_is_reported_as_missing()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Gamma"), Loc("Gamma") }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.Missing, result.Hint);
    }

    [Fact]
    public void An_unknown_name_inside_a_disagreeing_set_is_still_just_a_disagreement()
    {
        var result = DefaultTemplateResolver.Resolve(new[] { Loc("Gamma"), Loc("Alpha") }, KnownTemplates);

        Assert.Null(result.Template);
        Assert.Equal(DefaultTemplateHint.Differ, result.Hint);
    }
}
