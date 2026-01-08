using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using UnityEditor;
using UnityEngine;

namespace UnityAgentClient
{
    internal static class EditorMarkdownRenderer
    {
        static readonly MarkdownPipeline MarkdigPipeline = new MarkdownPipelineBuilder()
            .UseAutoLinks()
            .UsePipeTables(new PipeTableOptions { RequireHeaderSeparator = false })
            .Build();

        private enum BlockType
        {
            Paragraph,
            Heading1,
            Heading2,
            Heading3,
            Heading4,
            Heading5,
            Heading6,
            CodeBlock,
            ListItem,
            OrderedListItem,
            BlockQuote,
            HorizontalRule
        }

        private class MarkdownBlock
        {
            public BlockType Type { get; set; }
            public string Content { get; set; }
            public string Language { get; set; } // For code blocks
            public int IndentLevel { get; set; } // For lists
        }

        private class InlineStyle
        {
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
            public bool IsCode { get; set; }
            public bool IsStrikethrough { get; set; }
        }

        private static GUIStyle _headingStyle1;
        private static GUIStyle _headingStyle2;
        private static GUIStyle _headingStyle3;
        private static GUIStyle _headingStyle4;
        private static GUIStyle _paragraphStyle;
        private static GUIStyle _codeBlockStyle;
        private static GUIStyle _inlineCodeStyle;
        private static GUIStyle _boldStyle;
        private static GUIStyle _italicStyle;
        private static GUIStyle _boldItalicStyle;
        private static GUIStyle _listStyle;
        private static GUIStyle _quoteStyle;

        public static GUIStyle CodeBlockStyle
        {
            get
            {
                _codeBlockStyle ??= new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true,
                    padding = new RectOffset(8, 8, 8, 8),
                    margin = new RectOffset(0, 0, 4, 4),
                    fontSize = 11,
                    font = GUI.skin.font
                };
                return _codeBlockStyle;
            }
        }

        private static void InitializeStyles()
        {
            if (_headingStyle1 != null) return;

            var defaultFont = GUI.skin.font;

            // Heading 1
            _headingStyle1 = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 24,
                wordWrap = true,
                margin = new RectOffset(0, 0, 10, 10),
                font = defaultFont
            };

            // Heading 2
            _headingStyle2 = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 20,
                wordWrap = true,
                margin = new RectOffset(0, 0, 8, 8),
                font = defaultFont
            };

            // Heading 3
            _headingStyle3 = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                wordWrap = true,
                margin = new RectOffset(0, 0, 6, 6),
                font = defaultFont
            };

            // Heading 4
            _headingStyle4 = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                wordWrap = true,
                margin = new RectOffset(0, 0, 4, 4),
                font = defaultFont
            };

            // Paragraph
            _paragraphStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                margin = new RectOffset(0, 0, 4, 4),
                font = defaultFont
            };

            // Code block
            _codeBlockStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                fontSize = 11,
                font = defaultFont
            };

            // Inline code
            _inlineCodeStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                padding = new RectOffset(3, 3, 0, 0),
                normal = { background = MakeBackgroundTexture(new Color(0.2f, 0.2f, 0.2f, 0.3f)) },
                font = defaultFont
            };

            // Bold
            _boldStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                wordWrap = true,
                richText = true,
                margin = new RectOffset(0, 0, 4, 4),
                font = defaultFont
            };

            // Italic
            _italicStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                fontStyle = FontStyle.Italic,
                margin = new RectOffset(0, 0, 4, 4),
                font = defaultFont
            };

            // Bold + Italic
            _boldItalicStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                wordWrap = true,
                richText = true,
                fontStyle = FontStyle.BoldAndItalic,
                margin = new RectOffset(0, 0, 4, 4),
                font = defaultFont
            };

            // List
            _listStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                margin = new RectOffset(10, 0, 2, 2),
                font = defaultFont
            };

            // Quote
            _quoteStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(10, 0, 4, 4),
                normal = { background = MakeBackgroundTexture(new Color(0.3f, 0.3f, 0.3f, 0.3f)) },
                font = defaultFont
            };
        }

        private static Texture2D MakeBackgroundTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Render markdown text using IMGUI
        /// </summary>
        /// <param name="markdown">Markdown text to render</param>
        public static void Render(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return;
            }

            InitializeStyles();
            RenderWithMarkdig(markdown);
        }

        static void RenderWithMarkdig(string markdown)
        {
            MarkdownDocument doc;
            try
            {
                doc = Markdown.Parse(markdown, MarkdigPipeline);
            }
            catch
            {
                // Fallback: render raw if parser fails
                EditorGUILayout.TextArea(markdown, _codeBlockStyle);
                return;
            }

            RenderBlocks(doc, indentLevel: 0);
        }

        static void RenderBlocks(ContainerBlock container, int indentLevel)
        {
            if (container == null) return;

            foreach (var block in container)
            {
                RenderMarkdigBlock(block, indentLevel);
            }
        }

        static void RenderMarkdigBlock(Block block, int indentLevel)
        {
            if (block == null) return;

            switch (block)
            {
                case HeadingBlock heading:
                    RenderHeading(heading);
                    break;
                case ParagraphBlock paragraph:
                    RenderParagraph(paragraph, indentLevel);
                    break;
                case QuoteBlock quote:
                    RenderQuote(quote, indentLevel);
                    break;
                case ListBlock list:
                    RenderList(list, indentLevel);
                    break;
                case FencedCodeBlock fencedCode:
                    RenderCodeBlock(fencedCode.Lines.ToString());
                    break;
                case CodeBlock code:
                    RenderCodeBlock(code.Lines.ToString());
                    break;
                case ThematicBreakBlock:
                    GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    break;
                case Table table:
                    RenderTableAsCode(table);
                    break;
                default:
                    // Try render children if container, otherwise fallback to string.
                    if (block is ContainerBlock cb)
                    {
                        RenderBlocks(cb, indentLevel);
                    }
                    else
                    {
                        SelectableRichLabel(block.ToString(), _paragraphStyle);
                    }
                    break;
            }
        }

        static void RenderHeading(HeadingBlock heading)
        {
            var text = InlineToRichText(heading.Inline);

            var style = heading.Level switch
            {
                1 => _headingStyle1,
                2 => _headingStyle2,
                3 => _headingStyle3,
                _ => _headingStyle4
            };

            SelectableRichLabel(text, style);
        }

        static void RenderParagraph(ParagraphBlock paragraph, int indentLevel)
        {
            var text = InlineToRichText(paragraph.Inline);

            if (indentLevel > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indentLevel * 15);
                    SelectableRichLabel(text, _paragraphStyle);
                }
                return;
            }

            SelectableRichLabel(text, _paragraphStyle);
        }

        static void RenderQuote(QuoteBlock quote, int indentLevel)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                RenderBlocks(quote, indentLevel);
            }
        }

        static void RenderList(ListBlock list, int indentLevel)
        {
            var isOrdered = list.IsOrdered;
            var orderedIndex = 1;
            if (isOrdered && int.TryParse(list.OrderedStart, out var parsed))
            {
                orderedIndex = parsed;
            }

            foreach (var item in list)
            {
                if (item is not ListItemBlock listItem) continue;

                var bullet = isOrdered ? $"{orderedIndex}." : "•";
                orderedIndex++;

                // Render first paragraph inline on the bullet line if possible.
                ParagraphBlock firstParagraph = null;
                foreach (var child in listItem)
                {
                    firstParagraph = child as ParagraphBlock;
                    break;
                }
                var firstText = firstParagraph != null ? InlineToRichText(firstParagraph.Inline) : string.Empty;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indentLevel * 15);
                    SelectableRichLabel($"{bullet} {firstText}".TrimEnd(), _listStyle);
                }

                // Render remaining blocks in the item (skip the first paragraph we already printed).
                var skipFirst = true;
                foreach (var child in listItem)
                {
                    if (skipFirst && child is ParagraphBlock)
                    {
                        skipFirst = false;
                        continue;
                    }
                    RenderMarkdigBlock(child, indentLevel + 1);
                }
            }
        }

        static void RenderCodeBlock(string code)
        {
            if (code == null) code = string.Empty;
            EditorGUILayout.SelectableLabel(code, _codeBlockStyle, GUILayout.MinHeight(_codeBlockStyle.CalcHeight(new GUIContent(code), EditorGUIUtility.currentViewWidth)));
        }

        static void RenderTableAsCode(Table table)
        {
            // Simple fallback: render tables as pipe-delimited text so it remains readable & copyable.
            var sb = new StringBuilder();

            foreach (var rowObj in table)
            {
                if (rowObj is not TableRow row) continue;
                var cells = new List<string>();
                foreach (var cellObj in row)
                {
                    if (cellObj is not TableCell cell) continue;
                    cells.Add(ContainerToPlainText(cell));
                }
                sb.AppendLine(string.Join(" | ", cells));
            }

            RenderCodeBlock(sb.ToString().TrimEnd());
        }

        static string InlineToRichText(ContainerInline container)
        {
            if (container == null) return string.Empty;

            var sb = new StringBuilder();
            var inline = container.FirstChild;
            while (inline != null)
            {
                AppendInlineRich(sb, inline);
                inline = inline.NextSibling;
            }
            return sb.ToString();
        }

        static void AppendInlineRich(StringBuilder sb, Inline inline)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(EscapeRichText(lit.Content.ToString()));
                    break;
                case LineBreakInline:
                    sb.Append("\n");
                    break;
                case CodeInline code:
                    sb.Append($"<color=#D4D4D4><b>{EscapeRichText(code.Content)}</b></color>");
                    break;
                case EmphasisInline emph:
                    var (open, close) = GetEmphasisTags(emph);
                    sb.Append(open);
                    var child = emph.FirstChild;
                    while (child != null)
                    {
                        AppendInlineRich(sb, child);
                        child = child.NextSibling;
                    }
                    sb.Append(close);
                    break;
                case LinkInline link:
                    // Render link text; show URL in parentheses if there is no text.
                    var linkText = new StringBuilder();
                    var lc = link.FirstChild;
                    while (lc != null)
                    {
                        AppendInlineRich(linkText, lc);
                        lc = lc.NextSibling;
                    }
                    if (linkText.Length == 0 && !string.IsNullOrEmpty(link.Url))
                    {
                        sb.Append(EscapeRichText(link.Url));
                    }
                    else
                    {
                        sb.Append(linkText);
                    }
                    break;
                case ContainerInline ci:
                    var c = ci.FirstChild;
                    while (c != null)
                    {
                        AppendInlineRich(sb, c);
                        c = c.NextSibling;
                    }
                    break;
                default:
                    sb.Append(EscapeRichText(inline.ToString()));
                    break;
            }
        }

        static (string open, string close) GetEmphasisTags(EmphasisInline emph)
        {
            // Markdig 0.15.x uses IsDouble for strong emphasis (**bold**); otherwise treat as italic.
            return emph.IsDouble ? ("<b>", "</b>") : ("<i>", "</i>");
        }

        static string ContainerToPlainText(ContainerBlock container)
        {
            if (container == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var block in container)
            {
                if (block is ParagraphBlock p)
                {
                    sb.Append(InlineToPlainText(p.Inline));
                }
                else if (block is ContainerBlock cb)
                {
                    sb.Append(ContainerToPlainText(cb));
                }
                if (sb.Length > 0) sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        static string InlineToPlainText(ContainerInline container)
        {
            if (container == null) return string.Empty;
            var sb = new StringBuilder();
            var inline = container.FirstChild;
            while (inline != null)
            {
                switch (inline)
                {
                    case LiteralInline lit:
                        sb.Append(lit.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case LineBreakInline:
                        sb.Append("\n");
                        break;
                    case LinkInline link:
                        var child = link.FirstChild;
                        while (child != null)
                        {
                            if (child is LiteralInline cl) sb.Append(cl.Content.ToString());
                            child = child.NextSibling;
                        }
                        break;
                    case ContainerInline ci:
                        sb.Append(InlineToPlainText(ci));
                        break;
                }
                inline = inline.NextSibling;
            }
            return sb.ToString();
        }

        private static List<MarkdownBlock> ParseMarkdown(string markdown)
        {
            var blocks = new List<MarkdownBlock>();
            var lines = markdown.Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];

                // Skip empty lines
                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                // Code block (```)
                if (line.TrimStart().StartsWith("```"))
                {
                    var codeBlock = ParseCodeBlock(lines, ref i);
                    if (codeBlock != null)
                    {
                        blocks.Add(codeBlock);
                        continue;
                    }
                }

                // Horizontal rule
                if (Regex.IsMatch(line.Trim(), @"^(\*\*\*+|---+|___+)$"))
                {
                    blocks.Add(new MarkdownBlock { Type = BlockType.HorizontalRule });
                    i++;
                    continue;
                }

                // Headings
                var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (headingMatch.Success)
                {
                    var level = headingMatch.Groups[1].Length;
                    var content = headingMatch.Groups[2].Value;
                    blocks.Add(new MarkdownBlock
                    {
                        Type = level switch
                        {
                            1 => BlockType.Heading1,
                            2 => BlockType.Heading2,
                            3 => BlockType.Heading3,
                            4 => BlockType.Heading4,
                            5 => BlockType.Heading5,
                            _ => BlockType.Heading6
                        },
                        Content = content
                    });
                    i++;
                    continue;
                }

                // Ordered list
                var orderedListMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
                if (orderedListMatch.Success)
                {
                    var indent = orderedListMatch.Groups[1].Length;
                    var content = orderedListMatch.Groups[3].Value;
                    blocks.Add(new MarkdownBlock
                    {
                        Type = BlockType.OrderedListItem,
                        Content = content,
                        IndentLevel = indent / 2
                    });
                    i++;
                    continue;
                }

                // Unordered list
                var listMatch = Regex.Match(line, @"^(\s*)([-*+])\s+(.+)$");
                if (listMatch.Success)
                {
                    var indent = listMatch.Groups[1].Length;
                    var content = listMatch.Groups[3].Value;
                    blocks.Add(new MarkdownBlock
                    {
                        Type = BlockType.ListItem,
                        Content = content,
                        IndentLevel = indent / 2
                    });
                    i++;
                    continue;
                }

                // Block quote
                if (line.TrimStart().StartsWith(">"))
                {
                    var content = line.TrimStart().Substring(1).TrimStart();
                    blocks.Add(new MarkdownBlock
                    {
                        Type = BlockType.BlockQuote,
                        Content = content
                    });
                    i++;
                    continue;
                }

                // Regular paragraph
                var paragraph = ParseParagraph(lines, ref i);
                if (!string.IsNullOrWhiteSpace(paragraph))
                {
                    blocks.Add(new MarkdownBlock
                    {
                        Type = BlockType.Paragraph,
                        Content = paragraph
                    });
                }
            }

            return blocks;
        }

        private static MarkdownBlock ParseCodeBlock(string[] lines, ref int index)
        {
            var startLine = lines[index];
            var languageMatch = Regex.Match(startLine.TrimStart(), @"```(\w*)");
            var language = languageMatch.Groups[1].Value;

            index++; // Move past the opening ```

            var codeLines = new List<string>();
            while (index < lines.Length)
            {
                if (lines[index].TrimStart().StartsWith("```"))
                {
                    index++; // Move past the closing ```
                    break;
                }
                codeLines.Add(lines[index]);
                index++;
            }

            return new MarkdownBlock
            {
                Type = BlockType.CodeBlock,
                Content = string.Join("\n", codeLines),
                Language = language
            };
        }

        private static string ParseParagraph(string[] lines, ref int index)
        {
            var paragraphLines = new List<string>();

            while (index < lines.Length)
            {
                var line = lines[index];

                // Stop at empty line
                if (string.IsNullOrWhiteSpace(line))
                {
                    index++;
                    break;
                }

                // Stop at special syntax
                if (line.TrimStart().StartsWith("#") ||
                    line.TrimStart().StartsWith("```") ||
                    line.TrimStart().StartsWith(">") ||
                    Regex.IsMatch(line.Trim(), @"^[-*+]\s") ||
                    Regex.IsMatch(line.Trim(), @"^\d+\.\s") ||
                    Regex.IsMatch(line.Trim(), @"^(\*\*\*+|---+|___+)$"))
                {
                    break;
                }

                paragraphLines.Add(line);
                index++;
            }

            return string.Join(" ", paragraphLines);
        }

        private static void RenderBlock(MarkdownBlock block)
        {
            switch (block.Type)
            {
                case BlockType.Heading1:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _headingStyle1);
                    break;

                case BlockType.Heading2:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _headingStyle2);
                    break;

                case BlockType.Heading3:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _headingStyle3);
                    break;

                case BlockType.Heading4:
                case BlockType.Heading5:
                case BlockType.Heading6:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _headingStyle4);
                    break;

                case BlockType.CodeBlock:
                    EditorGUILayout.TextArea(block.Content, _codeBlockStyle);
                    break;

                case BlockType.ListItem:
                    var listIndent = block.IndentLevel * 15;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(listIndent);
                    SelectableRichLabel("• " + ProcessInlineMarkdown(block.Content), _listStyle);
                    EditorGUILayout.EndHorizontal();
                    break;

                case BlockType.OrderedListItem:
                    var orderedIndent = block.IndentLevel * 15;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(orderedIndent);
                    SelectableRichLabel("1. " + ProcessInlineMarkdown(block.Content), _listStyle);
                    EditorGUILayout.EndHorizontal();
                    break;

                case BlockType.BlockQuote:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _quoteStyle);
                    break;

                case BlockType.HorizontalRule:
                    GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    break;

                case BlockType.Paragraph:
                    SelectableRichLabel(ProcessInlineMarkdown(block.Content), _paragraphStyle);
                    break;
            }
        }

        static void SelectableRichLabel(string text, GUIStyle style)
        {
            // Make text copyable. IMGUI LabelField isn't selectable, but SelectableLabel is.
            // We also calculate height so wrapped text doesn't get clipped.
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var content = new GUIContent(text);
            var width = EditorGUIUtility.currentViewWidth - 40; // heuristic padding
            var height = style.CalcHeight(content, Mathf.Max(0, width));
            EditorGUILayout.SelectableLabel(text, style, GUILayout.MinHeight(height));
        }

        private static string ProcessInlineMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Process inline code first (to avoid processing markdown inside code)
            text = Regex.Replace(text, @"`([^`]+)`", match =>
            {
                var code = match.Groups[1].Value;
                return $"<color=#D4D4D4><b>{EscapeRichText(code)}</b></color>";
            });

            // Bold and Italic (***text*** or ___text___)
            text = Regex.Replace(text, @"(\*\*\*|___)(.+?)\1", "<b><i>$2</i></b>");

            // Bold (**text** or __text__)
            text = Regex.Replace(text, @"(\*\*|__)(.+?)\1", "<b>$2</b>");

            // Italic (*text* or _text_)
            text = Regex.Replace(text, @"(\*|_)(.+?)\1", "<i>$2</i>");

            // Strikethrough (~~text~~)
            text = Regex.Replace(text, @"~~(.+?)~~", "<s>$1</s>");

            // Links [text](url) - just show the text
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");

            return text;
        }

        private static string EscapeRichText(string text)
        {
            return text.Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}