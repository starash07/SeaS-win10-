using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SeaS.App.Services;

internal sealed partial class EpubImageStyles
{
    private sealed record Declaration(string Name, string Value, bool Important);
    private sealed record Rule(string Selector, int Specificity, List<Declaration> Declarations);
    private readonly List<Rule> _rules = [];
    private readonly Dictionary<XElement, Dictionary<string, string>> _elementStyles = [];

    public EpubImageStyles(EpubBookDocument document, EpubChapter chapter, XDocument html)
    {
        foreach (var element in html.Descendants())
        {
            string? css = null;
            if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                css = element.Value;
            }
            else if (element.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(element.Attribute("rel")?.Value, "stylesheet", StringComparison.OrdinalIgnoreCase)
                     && element.Attribute("href")?.Value is { } href
                     && document.ReadResourceBytes(chapter.Path, href) is { } bytes)
            {
                css = Encoding.UTF8.GetString(bytes);
            }

            if (css is not null)
            {
                ReadRules(css);
            }
        }
    }

    public Dictionary<string, string> GetStyles(XElement image)
    {
        if (_elementStyles.TryGetValue(image, out var cached))
        {
            return cached;
        }

        var values = new Dictionary<string, (string Value, bool Important, int Specificity)>();
        void Apply(Declaration declaration, int specificity)
        {
            if (values.TryGetValue(declaration.Name, out var previous)
                && (previous.Important && !declaration.Important
                    || previous.Important == declaration.Important && previous.Specificity > specificity))
            {
                return;
            }

            values[declaration.Name] = (declaration.Value, declaration.Important, specificity);
        }

        foreach (var name in new[] { "width", "height" })
        {
            if (image.Attribute(name)?.Value is { } value)
            {
                Apply(new Declaration(name, value, false), -1);
            }
        }

        var classes = (image.Attribute("class")?.Value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            var selector = ImageSelectorRegex().Match(rule.Selector);
            if (selector.Groups["tag"].Success
                && selector.Groups["tag"].Value != "*"
                && !selector.Groups["tag"].Value.Equals(image.Name.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (selector.Groups["id"].Captures.Cast<Capture>().Any(id => id.Value != image.Attribute("id")?.Value)
                || selector.Groups["class"].Captures.Cast<Capture>().Any(name => !classes.Contains(name.Value)))
            {
                continue;
            }

            foreach (var declaration in rule.Declarations)
            {
                Apply(declaration, rule.Specificity);
            }
        }

        foreach (var declaration in ReadDeclarations(image.Attribute("style")?.Value ?? string.Empty))
        {
            Apply(declaration, int.MaxValue);
        }

        var styles = values.ToDictionary(item => item.Key, item => item.Value.Value);
        _elementStyles[image] = styles;
        return styles;
    }

    private void ReadRules(string css)
    {
        css = CssCommentRegex().Replace(css, string.Empty).TrimStart('\uFEFF');
        var depth = 0;
        var ruleStart = 0;
        var bodyStart = 0;
        for (var index = 0; index < css.Length; index++)
        {
            if (css[index] is '\'' or '"')
            {
                var quote = css[index];
                for (index++; index < css.Length && css[index] != quote; index++)
                {
                    if (css[index] == '\\')
                    {
                        index++;
                    }
                }
                continue;
            }

            if (css[index] == '{')
            {
                if (depth == 0)
                {
                    bodyStart = index + 1;
                }
                depth++;
            }
            else if (css[index] == '}' && depth > 0)
            {
                depth--;
                if (depth != 0)
                {
                    continue;
                }

                var selectors = css[ruleStart..(bodyStart - 1)];
                var declarations = ReadDeclarations(css[bodyStart..index]);
                foreach (var selector in selectors.Split(',', StringSplitOptions.TrimEntries))
                {
                    var match = ImageSelectorRegex().Match(selector);
                    if (selector.Length == 0 || !match.Success || declarations.Count == 0)
                    {
                        continue;
                    }

                    var specificity = match.Groups["id"].Captures.Count * 10000
                                      + match.Groups["class"].Captures.Count * 100
                                      + (match.Groups["tag"].Success && match.Groups["tag"].Value != "*" ? 1 : 0);
                    _rules.Add(new Rule(selector, specificity, declarations));
                }

                ruleStart = index + 1;
            }
            else if (css[index] == ';' && depth == 0)
            {
                ruleStart = index + 1;
            }
        }
    }

    private static List<Declaration> ReadDeclarations(string body)
    {
        var declarations = new List<Declaration>();
        foreach (var part in body.Split(';'))
        {
            var separator = part.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = part[..separator].Trim().ToLowerInvariant();
            if (name is not ("width" or "height" or "max-width" or "max-height"
                or "display" or "vertical-align" or "text-align" or "margin-left" or "margin-right"))
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim();
            var important = CssImportantRegex().IsMatch(value);
            declarations.Add(new Declaration(name, CssImportantRegex().Replace(value, string.Empty).Trim(), important));
        }

        return declarations;
    }

    [GeneratedRegex(@"^(?<tag>\*|[a-zA-Z][\w-]*)?(?:\.(?<class>[\w-]+)|#(?<id>[\w-]+))*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageSelectorRegex();

    [GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex CssCommentRegex();

    [GeneratedRegex(@"!\s*important\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImportantRegex();
}
