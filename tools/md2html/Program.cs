using System.Text;
using System.Text.RegularExpressions;
using Markdig;

// usage: md2html <input.md> <output.html> <logo.png> <version>
var (input, output, logo, version) = (args[0], args[1], args[2], args[3]);
var md = File.ReadAllText(input);
var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseAutoIdentifiers().Build();
string body = Markdown.ToHtml(md, pipeline);

// Table of contents from the numbered "##" headings.
var toc = new StringBuilder("<ol class=\"toc\">");
foreach (Match m in Regex.Matches(body, "<h2 id=\"([^\"]+)\">(.*?)</h2>"))
    toc.Append($"<li><a href=\"#{m.Groups[1].Value}\">{m.Groups[2].Value}</a></li>");
toc.Append("</ol>");

// The first <h1> becomes the cover title.
body = Regex.Replace(body, "<h1 id=\"[^\"]+\">.*?</h1>", "", RegexOptions.Singleline);
string logoUri = new Uri(Path.GetFullPath(logo)).AbsoluteUri;
string baseUri = new Uri(Path.GetDirectoryName(Path.GetFullPath(input))! + Path.DirectorySeparatorChar).AbsoluteUri;

string html = $$"""
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>Wave Flume User Manual</title>
<base href="{{baseUri}}">
<style>
  @page { size: A4; margin: 18mm 16mm 18mm 16mm; }
  body { font-family: "Segoe UI", Arial, sans-serif; font-size: 10.5pt; line-height: 1.45; color: #1a1a1a; }
  .cover { height: 250mm; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; page-break-after: always; }
  .cover img { width: 62mm; height: 62mm; border: none; margin: 0; }
  .cover h1 { font-size: 34pt; margin: 14mm 0 2mm 0; color: #0b3c6e; }
  .cover p { font-size: 14pt; color: #505050; margin: 1mm 0; }
  .tocpage { page-break-after: always; }
  .tocpage h2 { color: #0b3c6e; border: none; }
  ol.toc { font-size: 12pt; line-height: 1.9; list-style: none; padding-left: 4mm; }
  ol.toc a { color: #1a1a1a; text-decoration: none; }
  h2 { color: #0b3c6e; font-size: 17pt; border-bottom: 2px solid #2f5f8f; padding-bottom: 2pt; margin-top: 18pt; page-break-after: avoid; }
  h3 { color: #2f5f8f; font-size: 12.5pt; margin-top: 13pt; page-break-after: avoid; }
  table { border-collapse: collapse; width: 100%; margin: 8pt 0; font-size: 9.5pt; page-break-inside: auto; }
  tr { page-break-inside: avoid; }
  th { background: #2f5f8f; color: #fff; text-align: left; padding: 4pt 6pt; }
  td { border: 1px solid #b8b8b8; padding: 4pt 6pt; vertical-align: top; }
  tr:nth-child(even) td { background: #f3f6fa; }
  code { font-family: Consolas, monospace; background: #eef1f5; padding: 0 3pt; border-radius: 2pt; font-size: 9.5pt; }
  blockquote { border-left: 4px solid #c00000; background: #fdf1f1; margin: 8pt 0; padding: 6pt 10pt; }
  blockquote p { margin: 0; }
  img { max-width: 100%; border: 1px solid #b8b8b8; margin: 6pt 0; }
  p > img { display: block; }
  hr { border: none; border-top: 1px solid #c8c8c8; }
</style></head><body>
<div class="cover"><img src="{{logoUri}}" alt="Wave Flume logo"><h1>Wave Flume</h1><p>User Manual</p><p>Version {{version}}</p></div>
<div class="tocpage"><h2>Contents</h2>{{toc}}</div>
{{body}}
</body></html>
""";
File.WriteAllText(output, html, new UTF8Encoding(false));
Console.WriteLine($"Wrote {output} ({html.Length:N0} characters)");
