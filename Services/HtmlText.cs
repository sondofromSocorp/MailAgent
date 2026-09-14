using System.Text;
using MimeKit.Text;

namespace MailAgent.Services;

/// <summary>
/// Convertit un corps HTML en texte lisible pour le modele : balises retirees, contenu des
/// blocs style/script/head ignore, sauts de ligne sur les elements de bloc, blancs compactes.
/// Sans cette conversion, un mail HTML-seul (courant en marketing mais aussi chez des
/// administrations) ne donnait au classifieur que des balises et du CSS dans l'apercu tronque.
/// </summary>
public static class HtmlText
{
    // Elements dont le CONTENU n'est pas du texte utile.
    private static readonly HashSet<HtmlTagId> Skipped =
        [HtmlTagId.Style, HtmlTagId.Script, HtmlTagId.Head, HtmlTagId.Title, HtmlTagId.NoScript];

    // Elements qui coupent le flux : un saut de ligne avant/apres.
    private static readonly HashSet<HtmlTagId> Blocks =
    [
        HtmlTagId.P, HtmlTagId.Div, HtmlTagId.Br, HtmlTagId.LI, HtmlTagId.UL, HtmlTagId.OL, HtmlTagId.TR,
        HtmlTagId.Table, HtmlTagId.H1, HtmlTagId.H2, HtmlTagId.H3, HtmlTagId.H4, HtmlTagId.H5, HtmlTagId.H6,
        HtmlTagId.HR, HtmlTagId.BlockQuote, HtmlTagId.Pre, HtmlTagId.Section, HtmlTagId.Article,
        HtmlTagId.Header, HtmlTagId.Footer, HtmlTagId.Nav, HtmlTagId.Center,
    ];

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var sb = new StringBuilder(html.Length / 4);
        var skipDepth = 0;
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                {
                    var tag = (HtmlTagToken)token;
                    if (Skipped.Contains(tag.Id) && !tag.IsEmptyElement)
                    {
                        if (tag.IsEndTag) skipDepth = Math.Max(0, skipDepth - 1);
                        else skipDepth++;
                    }
                    else if (Blocks.Contains(tag.Id)) sb.Append('\n');
                    else if (tag.Id == HtmlTagId.TD || tag.Id == HtmlTagId.TH) sb.Append(' ');
                    break;
                }
                case HtmlTokenKind.Data:
                case HtmlTokenKind.CData:
                    if (skipDepth == 0) sb.Append(((HtmlDataToken)token).Data);
                    break;
                // ScriptData, Comment, DocType : ignores.
            }
        }

        return Compact(sb.ToString());
    }

    /// <summary>Compacte les blancs : suites d'espaces -> un espace, plus de deux sauts de ligne -> deux.</summary>
    private static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        var newlines = 0;
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (c == '\n' || c == '\r')
            {
                if (newlines < 2) { sb.Append('\n'); newlines++; }
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(c) || c == ' ')
            {
                pendingSpace = true;
            }
            else
            {
                if (pendingSpace && newlines == 0 && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
                newlines = 0;
            }
        }
        return sb.ToString().Trim();
    }
}
