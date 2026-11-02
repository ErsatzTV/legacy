using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace ErsatzTV.Shared;

public partial class JsonView
{
    private static readonly Regex Token = new(
        """(?<key>"(?:\\.|[^"\\])*")(?=\s*:)|(?<string>"(?:\\.|[^"\\])*")|(?<number>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<literal>\b(?:true|false|null)\b)""",
        RegexOptions.Compiled);

    private static readonly string[] Groups = ["key", "string", "number", "literal"];

    private string _json;
    private MarkupString _highlighted;

    [Parameter]
    public string Json { get; set; }

    [Parameter]
    public string Class { get; set; }

    public ElementReference CodeElement { get; private set; }

    private MarkupString Highlighted => _highlighted;

    protected override void OnParametersSet()
    {
        if (!string.Equals(_json, Json, StringComparison.Ordinal))
        {
            _json = Json;
            _highlighted = Highlight(Json);
        }
    }

    // spans only wrap the original text, so copy (textContent) is unchanged
    private static MarkupString Highlight(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new MarkupString();
        }

        // callers may pass exception text; the token regex needs valid json
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new MarkupString(WebUtility.HtmlEncode(json));
        }

        var html = new StringBuilder(json.Length * 2);
        var position = 0;
        foreach (Match match in Token.Matches(json))
        {
            html.Append(WebUtility.HtmlEncode(json[position..match.Index]));

            string kind = Groups.First(g => match.Groups[g].Success);
            html.Append("<span class=\"json-").Append(kind).Append("\">")
                .Append(WebUtility.HtmlEncode(match.Value))
                .Append("</span>");

            position = match.Index + match.Length;
        }

        html.Append(WebUtility.HtmlEncode(json[position..]));

        return new MarkupString(html.ToString());
    }
}
