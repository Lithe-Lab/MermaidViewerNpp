using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace NppMermaidViewer
{
	internal sealed class MarkdownRenderer
	{
		private static readonly Regex MermaidFence = new Regex(@"^[ \t]*```[ \t]*mermaid[ \t]*\r?\n(?<code>.*?)(?:\r?\n)[ \t]*```[ \t]*(?=\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
		private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

		public string Render(string markdown, string mermaidScript)
		{
			return BuildDocument(RenderBody(markdown), mermaidScript);
		}

		public string RenderBody(string markdown, bool isMermaidFile = false)
		{
			if (markdown == null) throw new ArgumentNullException(nameof(markdown));
			if (isMermaidFile)
				return CreateMermaidElement(0, markdown);

			StringBuilder source = new StringBuilder();
			List<string> mermaidElements = new List<string>();
			int position = 0;
			int diagramIndex = 0;
			foreach (Match match in MermaidFence.Matches(markdown))
			{
				source.Append(markdown.Substring(position, match.Index - position));
				source.Append(GetMermaidMarker(diagramIndex));
				mermaidElements.Add(CreateMermaidElement(diagramIndex, match.Groups["code"].Value));
				diagramIndex++;
				position = match.Index + match.Length;
			}
			source.Append(markdown.Substring(position));
			string body = Markdown.ToHtml(source.ToString(), pipeline);
			for (int index = 0; index < mermaidElements.Count; index++)
				body = body.Replace(GetMermaidMarker(index), mermaidElements[index]);
			return body;
		}

		private static string GetMermaidMarker(int diagramIndex)
		{
			return "<!--mermaid-diagram-" + diagramIndex + "-->";
		}

		private static string CreateMermaidElement(int diagramIndex, string code)
		{
			return "<div class=\"mermaid\" id=\"mermaid-diagram-" + diagramIndex + "\">" +
				WebUtility.HtmlEncode(code) + "</div>";
		}

		private static string BuildDocument(string body, string mermaidScript)
		{
			return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
				"body{font-family:Segoe UI,Meiryo,sans-serif;margin:16px;color:#202124;background:#fff;}" +
				"pre{background:#f5f5f5;padding:12px;overflow:auto;}code{font-family:Consolas,monospace;}" +
				"table{border-collapse:collapse;}th,td{border:1px solid #aaa;padding:4px 8px;}.mermaid{margin:16px 0;overflow:auto;}" +
				"</style></head><body>" + body + "<script>" + mermaidScript +
				"</script><script>mermaid.initialize({startOnLoad:false,securityLevel:'strict'});mermaid.run({querySelector:'.mermaid'});</script></body></html>";
		}
	}
}
