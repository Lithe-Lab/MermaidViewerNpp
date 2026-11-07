using System;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Microsoft.WindowsAPICodePack.Dialogs;
using NppMermaidViewer.Utils;

namespace NppMermaidViewer
{
	internal sealed class MermaidViewerForm : Form
	{
		private readonly WebView2 browser = new WebView2();
		private readonly MarkdownRenderer renderer = new MarkdownRenderer();
		private readonly object renderLock = new object();
		private readonly SemaphoreSlim initializationLock = new SemaphoreSlim(1, 1);
		private bool initialized;
		private bool outlineVisible;
		private string syncScrollMode = "CodeToPreview";
		private string mermaidScript;
		private int renderRequestVersion;
		private TaskCompletionSource<string> exportDataRequest;
		private string pendingExportFormat;

		public event Action<double> ScrollSyncRequested;
		public event Action SyncScrollModeChangeRequested;
		public event Action<bool> OutlineVisibilityChanged;
		public event Action<bool> ThemeChangeRequested;

		public MermaidViewerForm()
		{
			Text = "Mermaid Viewer";
			ShowInTaskbar = false;
			Width = 520;
			Height = 720;
			string userDataFolder = GetWebViewUserDataFolder();
			Directory.CreateDirectory(userDataFolder);
			// Set CreationProperties before the browser handle is created.
			// Setting it after handle creation can conflict with automatic initialization using default settings and cause E_INVALIDARG.
			browser.CreationProperties = new CoreWebView2CreationProperties
			{
				UserDataFolder = userDataFolder
			};
			browser.Dock = DockStyle.Fill;
			Controls.Add(browser);
		}

		public async Task SetSyncScrollModeAsync(string mode)
		{
			switch (mode)
			{
				case "CodeToPreview":
				case "PreviewToCode":
				case "CodeAndPreview":
				case "Off":
					syncScrollMode = mode;
					break;
				default:
					syncScrollMode = "CodeToPreview";
					break;
			}

			if (!initialized || IsDisposed || browser.IsDisposed || browser.CoreWebView2 == null)
				return;

			try
			{
				await browser.CoreWebView2.ExecuteScriptAsync(
					"window.setSyncScrollMode('" + syncScrollMode + "');").ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				Logger.LogException("MermaidViewerForm.SetSyncScrollModeAsync", exception);
			}
		}

		public async Task InitializeAsync()
		{
			if (initialized) return;
			await initializationLock.WaitAsync().ConfigureAwait(true);
			string diagnostics = null;
			try
			{
				if (initialized) return;
				string scriptPath = GetMermaidScriptPath();
				if (scriptPath == null)
					throw new FileNotFoundException("Mermaid.js could not be found.", scriptPath);
				await WaitForValidBoundsAsync().ConfigureAwait(true);
				diagnostics = string.Format(
					"IsHandleCreated={0}, ClientSize={1}, UserDataFolder={2}, BrowserExecutableFolder={3}, RuntimeVersion={4}",
					browser.IsHandleCreated, browser.ClientSize,
					browser.CreationProperties != null ? browser.CreationProperties.UserDataFolder : "(null)",
					browser.CreationProperties != null ? browser.CreationProperties.BrowserExecutableFolder : "(null)",
					GetRuntimeVersionSafe());
				Logger.LogInfo("MermaidViewerForm.InitializeAsync", diagnostics);
				await browser.EnsureCoreWebView2Async().ConfigureAwait(true);
				browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
				browser.ZoomFactorChanged += OnZoomFactorChanged;
				mermaidScript = await Task.Run(() => File.ReadAllText(scriptPath)).ConfigureAwait(true);
				await NavigateToHostAsync().ConfigureAwait(true);
				initialized = true;
			}

			catch (Exception exception)
			{
				Logger.LogInfo("MermaidViewerForm.InitializeAsync failure context", diagnostics);
				Logger.LogException("MermaidViewerForm.InitializeAsync", exception);
				throw;
			}
			finally
			{
				initializationLock.Release();
			}
		}

		private async void OnZoomFactorChanged(object sender, object e)
		{
			await ApplyToolbarZoomAsync().ConfigureAwait(true);
		}

		private async Task ApplyToolbarZoomAsync()
		{
			if (browser.IsDisposed || browser.CoreWebView2 == null)
				return;

			try
			{
				await browser.CoreWebView2.ExecuteScriptAsync(
					"window.setToolbarZoom(" + browser.ZoomFactor.ToString("R", CultureInfo.InvariantCulture) + ");").ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				Logger.LogException("MermaidViewerForm.ApplyToolbarZoomAsync", exception);
			}
		}

		private static string GetRuntimeVersionSafe()
		{
			try
			{
				return CoreWebView2Environment.GetAvailableBrowserVersionString();
			}
			catch (Exception exception)
			{
				return "Failed to retrieve: " + exception.Message;
			}
		}

		private async Task WaitForValidBoundsAsync()
		{
			const int maxAttempts = 100;
			const int delayMilliseconds = 50;
			for (int attempt = 0; attempt < maxAttempts; attempt++)
			{
				if (browser.IsHandleCreated && browser.ClientSize.Width > 0 && browser.ClientSize.Height > 0)
					return;
				await Task.Delay(delayMilliseconds).ConfigureAwait(true);
			}
			// If the size is still zero after waiting, let the subsequent EnsureCoreWebView2Async call handle the error.
		}

		public async Task RenderAsync(string markdown, string theme, bool isMermaidFile = false)
		{
			if (IsDisposed || browser.IsDisposed)
				return;
			await InitializeAsync().ConfigureAwait(true);
			if (IsDisposed || browser.IsDisposed)
				return;
			await ApplyOutlineOptionsAsync().ConfigureAwait(true);
			int requestVersion = Interlocked.Increment(ref renderRequestVersion);
			string body = await Task.Run(() => RenderBody(markdown ?? string.Empty, isMermaidFile)).ConfigureAwait(true);
			if (requestVersion != Volatile.Read(ref renderRequestVersion))
				return;
			if (IsDisposed || browser.IsDisposed)
				return;
			CoreWebView2 coreWebView2 = browser.CoreWebView2;
			if (coreWebView2 == null)
				return;
			await coreWebView2.ExecuteScriptAsync("window.updateMermaidPreview(\"" + EscapeJavaScriptString(body) + "\", \"" + theme + "\");").ConfigureAwait(true);
		}

		public Task SetOutlineVisibleAsync(bool visible)
		{
			outlineVisible = visible;
			return ApplyOutlineOptionsAsync();
		}

		public async Task ExportAsync(string format, bool includeOutline)
		{
			if (!initialized || IsDisposed || browser.IsDisposed || browser.CoreWebView2 == null)
				return;

			if (format == "html")
				await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('export-with-outline').checked=" + (includeOutline ? "true" : "false") + ";").ConfigureAwait(true);

			HandleExportRequested(format);
		}

		private async Task ApplyOutlineOptionsAsync()
		{
			if (!initialized || IsDisposed || browser.IsDisposed || browser.CoreWebView2 == null)
				return;

			try
			{
				await browser.CoreWebView2.ExecuteScriptAsync(
					"window.setOutlineVisible(" + (outlineVisible ? "true" : "false") + ");").ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				Logger.LogException("MermaidViewerForm.ApplyOutlineOptionsAsync", exception);
			}
		}

		public async Task SyncPreviewScrollAsync(double scrollRatio)
		{
			if (!initialized || IsDisposed || browser.CoreWebView2 == null)
				return;

			scrollRatio = Math.Max(0, Math.Min(1, scrollRatio));
			try
			{
				await browser.CoreWebView2.ExecuteScriptAsync(
					"window.syncPreviewScroll(" + scrollRatio.ToString("R", CultureInfo.InvariantCulture) + ");").ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				Logger.LogException("MermaidViewerForm.SyncPreviewScrollAsync", exception);
			}
		}

		private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
		{
			string message = e.TryGetWebMessageAsString();
			const string exportPrefix = "export:";
			if (message.StartsWith(exportPrefix, StringComparison.Ordinal))
			{
				string format = message.Substring(exportPrefix.Length);
				if (format == "html" || format == "png-individual" || format == "svg-individual")
					HandleExportRequested(format);
				return;
			}
			const string exportDataPrefix = "export-data:";
			if (message.StartsWith(exportDataPrefix, StringComparison.Ordinal) && exportDataRequest != null)
			{
				int formatSeparator = message.IndexOf(':', exportDataPrefix.Length);
				if (formatSeparator > 0 && message.Substring(exportDataPrefix.Length, formatSeparator - exportDataPrefix.Length) == pendingExportFormat)
					exportDataRequest.TrySetResult(message.Substring(formatSeparator + 1));
				return;
			}
			const string outlinePrefix = "outline:";
			if (message.StartsWith(outlinePrefix, StringComparison.Ordinal) && bool.TryParse(message.Substring(outlinePrefix.Length), out bool outlineIsVisible))
			{
				OutlineVisibilityChanged?.Invoke(outlineIsVisible);
				return;
			}
			const string themePrefix = "theme:";
			if (message.StartsWith(themePrefix, StringComparison.Ordinal) && bool.TryParse(message.Substring(themePrefix.Length), out bool darkTheme))
			{
				ThemeChangeRequested?.Invoke(darkTheme);
				return;
			}
			if (message == "sync-mode:next")
			{
				SyncScrollModeChangeRequested?.Invoke();
				return;
			}
			if (double.TryParse(message, NumberStyles.Float, CultureInfo.InvariantCulture, out double scrollRatio))
				ScrollSyncRequested?.Invoke(Math.Max(0, Math.Min(1, scrollRatio)));
		}

		private async void HandleExportRequested(string format)
		{
			if (exportDataRequest != null)
				return;

			TaskCompletionSource<string> request = new TaskCompletionSource<string>();
			exportDataRequest = request;
			pendingExportFormat = format;
			string htmlOutputPath = null;
			string individualOutputDirectory = null;
			string individualSourceName = null;
			try
			{
				if (format == "html")
				{
					string sourcePath = NppDemo.Utils.Npp.GetCurrentPath();
					string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
					if (string.IsNullOrWhiteSpace(sourceName))
						sourceName = "Untitled";
					using (SaveFileDialog dialog = new SaveFileDialog())
					{
						dialog.Title = "Export HTML";
						dialog.Filter = "HTML files (*.html)|*.html";
						dialog.DefaultExt = "html";
						dialog.AddExtension = true;
						dialog.OverwritePrompt = true;
						dialog.FileName = sourceName + ".html";
						if (dialog.ShowDialog(this) != DialogResult.OK)
							return;
						htmlOutputPath = dialog.FileName;
					}
				}
				else if (format == "png-individual" || format == "svg-individual")
				{
					string sourcePath = NppDemo.Utils.Npp.GetCurrentPath();
					individualSourceName = Path.GetFileNameWithoutExtension(sourcePath);
					if (string.IsNullOrWhiteSpace(individualSourceName))
						individualSourceName = "Untitled";
					individualOutputDirectory = Path.GetDirectoryName(sourcePath);
					if (string.IsNullOrWhiteSpace(individualOutputDirectory) || !Directory.Exists(individualOutputDirectory))
						individualOutputDirectory = Environment.CurrentDirectory;
					using (CommonOpenFileDialog dialog = new CommonOpenFileDialog())
					{
						dialog.Title = "Select a folder to export Mermaid diagrams to.";
						dialog.IsFolderPicker = true;
						dialog.InitialDirectory = individualOutputDirectory;
						dialog.AllowNonFileSystemItems = false;
						dialog.EnsurePathExists = true;
						dialog.Multiselect = false;
						if (dialog.ShowDialog(this.Handle) != CommonFileDialogResult.Ok)
							return;
						individualOutputDirectory = dialog.FileName;
					}
				}

				string exportScript = format == "html"
					? "window.exportPreviewHtml();"
					: "window.exportIndividualDiagrams('" + format.Substring(0, format.IndexOf('-')) + "');";
				await browser.CoreWebView2.ExecuteScriptAsync(exportScript).ConfigureAwait(true);
				Task completed = await Task.WhenAny(request.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
				if (completed != request.Task)
					throw new TimeoutException("Timed out while retrieving export data.");

				string data = await request.Task.ConfigureAwait(true);
				if (data == "NO_DIAGRAM")
				{
					MessageBox.Show(this, "There are no Mermaid diagrams to export.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
				if (data == "SVG_RENDER_FAILED")
				{
					MessageBox.Show(this, "Mermaid diagrams are still rendering and cannot be exported yet.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				if (format == "html")
				{
					File.WriteAllBytes(htmlOutputPath, Convert.FromBase64String(data));
					return;
				}
				if (format == "png-individual" || format == "svg-individual")
				{
					string individualExtension = format.Substring(0, format.IndexOf('-'));
					string[] diagrams = data.Split(';');
					int outputCount = Math.Min(diagrams.Length, 99);
					for (int index = 0; index < outputCount; index++)
					{
						string fileName = individualSourceName + "_mmd_" + (index + 1).ToString("D2", CultureInfo.InvariantCulture) + "." + individualExtension;
						File.WriteAllBytes(Path.Combine(individualOutputDirectory, fileName), Convert.FromBase64String(diagrams[index]));
					}
					return;
				}
			}
			catch (Exception exception)
			{
				Logger.LogException("MermaidViewerForm.HandleExportRequested", exception);
				MessageBox.Show(this, "Export failed." + Environment.NewLine + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			finally
			{
				exportDataRequest = null;
				pendingExportFormat = null;
			}
		}

		private string RenderBody(string markdown, bool isMermaidFile)
		{
			lock (renderLock)
			{
				return renderer.RenderBody(markdown, isMermaidFile);
			}
		}

		private async Task NavigateToHostAsync()
		{
			TaskCompletionSource<bool> navigationCompleted = new TaskCompletionSource<bool>();
			EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (sender, args) =>
			{
				if (args.IsSuccess)
					navigationCompleted.TrySetResult(true);
				else
					navigationCompleted.TrySetException(new InvalidOperationException("Failed to initialize the preview page."));
			};
			browser.CoreWebView2.NavigationCompleted += handler;
			try
			{
				string hostFilePath = GetHostFilePath();
				File.WriteAllText(hostFilePath, BuildHostDocument(), new UTF8Encoding(false));
				browser.CoreWebView2.Navigate(new Uri(hostFilePath).AbsoluteUri);
				await navigationCompleted.Task.ConfigureAwait(true);
			}
			finally
			{
				browser.CoreWebView2.NavigationCompleted -= handler;
			}
			await ApplyToolbarZoomAsync().ConfigureAwait(true);
		}

		private string BuildHostDocument()
		{
			string openIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("left_panel_open.svg")));
			string closeIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("left_panel_close.svg")));
			string darkModeIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("dark_mode.svg")));
			string lightModeIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("light_mode.svg")));
			string codeBlocksIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("code_blocks.svg")));
			string filterNoneIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("filter_none.svg")));
			string photoLibraryIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("photo_library.svg")));
			string articleIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("article.svg")));
			string listAltIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("list_alt.svg")));
			string twoPagerIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("two_pager.svg")));
			string mobileDataOffIconUri = "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(GetOutlineIconPath("mobiledata_off.svg")));
			string document = "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
				"*{box-sizing:border-box;}body{font-family:Segoe UI,Meiryo,sans-serif;margin:0;color:#202124;background:#fff;}" +
				"body.dark{color:#e8eaed;background:#202124;}body.dark pre{background:#303134;}body.dark th,body.dark td{border-color:#5f6368;}" +
				"pre{background:#f5f5f5;padding:12px;overflow:auto;}code{font-family:Consolas,monospace;}" +
				"table{border-collapse:collapse;}th,td{border:1px solid #aaa;padding:4px 8px;}.mermaid{margin:16px 0;overflow:auto;}" +
				"#toolbar{position:fixed;z-index:4;top:0;left:0;right:0;height:25px;display:flex;align-items:center;gap:4px;padding:2px 8px;background:#fff;border-bottom:1px solid #dadce0;}body.dark #toolbar{background:#202124;border-color:#5f6368;}" +
				"#toolbar button{height:27px;padding:0 12px;border:0;border-radius:4px;background:transparent;color:inherit;font:inherit;cursor:pointer;}#toolbar button:hover{background:#00000014;}body.dark #toolbar button:hover{background:#ffffff1f;}#export-outline-option{display:flex;align-items:center;gap:4px;padding:0 6px;font-size:10px;}" +
				"#preview{min-height:100vh;padding:47px 16px 16px;transition:margin-left .2s ease;}" +
				"#outline{position:fixed;z-index:2;top:35px;bottom:0;left:0;width:280px;padding:16px;overflow:auto;background:#f5f5f5;border-right:1px solid #dadce0;transform:translateX(-100%);transition:transform .2s ease;}" +
				"body.dark #outline{background:#303134;border-color:#5f6368;}body.outline-open #outline{transform:translateX(0);}body.outline-open #preview{margin-left:280px;}" +
				"#toolbar #outline-toggle{width:36px;padding:0;margin-right:auto;}" +
				"#outline-icon,#theme-icon,#code-blocks-icon,#filter-none-icon,#photo-library-icon,#sync-scroll-icon{width:20px;height:20px;display:block;}body.dark #outline-icon,body.dark #theme-icon,body.dark #code-blocks-icon,body.dark #filter-none-icon,body.dark #photo-library-icon,body.dark #sync-scroll-icon{filter:brightness(0) invert(80%);}#toolbar #theme-toggle{width:36px;padding:0;margin-left:16px;order:1;}#toolbar #sync-scroll-toggle{display:flex;flex:0 0 135px;align-items:center;justify-content:flex-start;gap:4px;padding:0 6px;font-size:10px;white-space:nowrap;}#toolbar #html-export,#toolbar [data-export$='-individual']{display:flex;align-items:center;gap:4px;padding-right:0;font-size:10px;}#outline h2{margin:0 0 12px;font-size:16px;}" +
                "#outline-items{display:flex;flex-direction:column;gap:0;}#outline-items button{border:0;border-radius:4px;padding:3px 8px;text-align:left;background:transparent;color:inherit;cursor:pointer;}#outline-items button:hover{background:#00000014;}" +
				"</style></head><body><header id=\"toolbar\"><button id=\"outline-toggle\" type=\"button\" aria-label=\"Outline\" aria-expanded=\"false\" title=\"Outline\"><span class=\"material-symbols-outlined\">left_panel_open</span></button>" +
				"<button id=\"sync-scroll-toggle\" type=\"button\" title=\"Sync scroll\"><img id=\"sync-scroll-icon\" src=\"" + GetSyncScrollIconUri(syncScrollMode, articleIconUri, listAltIconUri, twoPagerIconUri, mobileDataOffIconUri) + "\" alt=\"\"><span id=\"sync-scroll-label\">" + GetSyncScrollLabel(syncScrollMode).Replace("<", "&lt;").Replace(">", "&gt;") + "</span></button>" +
				"<button id=\"theme-toggle\" type=\"button\" aria-label=\"Switch to dark mode\" title=\"Switch to dark mode\"><img id=\"theme-icon\" src=\"" + darkModeIconUri + "\" alt=\"\"></button><button id=\"html-export\" type=\"button\" data-export=\"html\"><img id=\"code-blocks-icon\" src=\"" + codeBlocksIconUri + "\" alt=\"\">HTML</button>" +
				"<label id=\"export-outline-option\"><input id=\"export-with-outline\" type=\"checkbox\">Include outline</label><button type=\"button\" data-export=\"png-individual\"><img id=\"photo-library-icon\" src=\"" + photoLibraryIconUri + "\" alt=\"\">PNG</button><button type=\"button\" data-export=\"svg-individual\"><img id=\"filter-none-icon\" src=\"" + filterNoneIconUri + "\" alt=\"\">SVG</button></header><aside id=\"outline\"><h2>Outline</h2>" +
				"<nav id=\"outline-items\"></nav></aside><main id=\"preview\"></main><script>" + mermaidScript +
				"</script><script>mermaid.initialize({startOnLoad:false,securityLevel:'strict'});var syncingFromSource=false;window.setOutlineVisible=function(visible,notify){document.body.classList.toggle('outline-open',visible);var button=document.getElementById('outline-toggle');var icon=button.querySelector('img');icon.src=visible?'" + closeIconUri + "':'" + openIconUri + "';button.setAttribute('aria-label',visible?'Close outline':'Open outline');button.setAttribute('title',visible?'Close outline':'Open outline');button.setAttribute('aria-expanded',String(visible));if(notify)chrome.webview.postMessage('outline:'+String(visible));};document.getElementById('outline-toggle').addEventListener('click',function(){window.setOutlineVisible(!document.body.classList.contains('outline-open'),true);});document.querySelectorAll('[data-export]').forEach(function(button){button.addEventListener('click',function(){chrome.webview.postMessage('export:'+button.getAttribute('data-export'));});});window.updateOutline=function(){var list=document.getElementById('outline-items');list.replaceChildren();var count=0;var headings=document.querySelectorAll('#preview h1, #preview h2, #preview h3, #preview h4, #preview h5, #preview h6');headings.forEach(function(heading){var label=heading.textContent.replace(/\\s+/g,' ').trim();if(!label)return;var item=document.createElement('button');item.type='button';item.textContent=label;item.style.paddingLeft=(8+(parseInt(heading.tagName.substring(1),10)-1)*12)+'px';item.addEventListener('click',function(){heading.scrollIntoView({behavior:'smooth',block:'center'});});list.appendChild(item);count++;});if(count===0)list.textContent='No outline available.';};window.syncPreviewScroll=function(ratio){window.lastScrollRatio=ratio;syncingFromSource=true;var maxScroll=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);window.scrollTo(0,ratio*maxScroll);setTimeout(function(){syncingFromSource=false;},100);};window.addEventListener('scroll',function(){if(syncingFromSource)return;var maxScroll=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);chrome.webview.postMessage(String(maxScroll===0?0:window.scrollY/maxScroll));},{passive:true});window.updateMermaidPreview=function(html,theme){document.body.className=(theme==='dark'?'dark ':'')+(document.body.classList.contains('outline-open')?'outline-open':'');mermaid.initialize({startOnLoad:false,securityLevel:'strict',theme:theme});var preview=document.getElementById('preview');preview.innerHTML=html;mermaid.run({nodes:preview.querySelectorAll('.mermaid')}).then(function(){window.updateOutline();if(typeof window.lastScrollRatio==='number')window.syncPreviewScroll(window.lastScrollRatio);});};</script></body></html>";
			document = document.Replace("+'</body></html>';", "+'</body>'+ '</html>';");
			document = document
				.Replace("<span class=\"material-symbols-outlined\">left_panel_open</span>", "<img id=\"outline-icon\" src=\"" + openIconUri + "\" alt=\"\">")
				.Replace("<aside id=\"outline\"><h2>Outline</h2>", "<aside id=\"outline\">")
				.Replace("mermaid.initialize({startOnLoad:false,securityLevel:'strict'});", "var toolbarZoom=1;window.setToolbarZoom=function(zoom){toolbarZoom=zoom;var toolbar=document.getElementById('toolbar');if(!toolbar)return;toolbar.style.transform='scale('+(1/zoom)+')';toolbar.style.transformOrigin='top left';toolbar.style.width=(window.innerWidth*zoom)+'px';};window.addEventListener('resize',function(){window.setToolbarZoom(toolbarZoom);});mermaid.initialize({startOnLoad:false,securityLevel:'strict'});")
				.Replace("var syncingFromSource=false;", "var syncingFromSource=false;window.setTheme=function(theme,notify){var dark=theme==='dark';document.body.classList.toggle('dark',dark);var icon=document.getElementById('theme-icon');icon.src=dark?'" + lightModeIconUri + "':'" + darkModeIconUri + "';var label=dark?'Switch to light mode':'Switch to dark mode';var button=document.getElementById('theme-toggle');button.setAttribute('aria-label',label);button.setAttribute('title',label);if(notify)chrome.webview.postMessage('theme:'+String(dark));};document.getElementById('theme-toggle').addEventListener('click',function(){window.setTheme(document.body.classList.contains('dark')?'default':'dark',true);});")
				.Replace("mermaid.initialize({startOnLoad:false,securityLevel:'strict',theme:theme});", "window.setTheme(theme,false);mermaid.initialize({startOnLoad:false,securityLevel:'strict',theme:theme});");
			string syncModeScript = "window.setSyncScrollMode=function(mode){var modes={CodeToPreview:{label:'Code -> Preview',icon:'" + articleIconUri + "'},PreviewToCode:{label:'Preview -> Code',icon:'" + listAltIconUri + "'},CodeAndPreview:{label:'Code <-> Preview',icon:'" + twoPagerIconUri + "'},Off:{label:'Off',icon:'" + mobileDataOffIconUri + "'}};var current=modes[mode]||modes.CodeToPreview;document.getElementById('sync-scroll-icon').src=current.icon;document.getElementById('sync-scroll-label').textContent=current.label;var button=document.getElementById('sync-scroll-toggle');button.setAttribute('aria-label','Sync scroll ('+current.label+')');button.setAttribute('title','Sync scroll ('+current.label+')');};window.setSyncScrollMode('" + syncScrollMode + "');document.getElementById('sync-scroll-toggle').addEventListener('click',function(){chrome.webview.postMessage('sync-mode:next');});";
			document = document.Replace("window.setTheme=function(theme,notify){", syncModeScript + "window.setTheme=function(theme,notify){");
			int closingTagIndex = document.LastIndexOf("</body></html>", StringComparison.Ordinal);
			return document.Insert(closingTagIndex, BuildHtmlExportScript() + BuildIndividualExportScript());
		}

		private static string GetSyncScrollIconUri(string mode, string codeToPreview, string previewToCode, string codeAndPreview, string off)
		{
			switch (mode)
			{
				case "PreviewToCode":
					return previewToCode;
				case "CodeAndPreview":
					return codeAndPreview;
				case "Off":
					return off;
				default:
					return codeToPreview;
			}
		}

		private static string GetSyncScrollLabel(string mode)
		{
			switch (mode)
			{
				case "PreviewToCode":
					return "Preview -> Code";
				case "CodeAndPreview":
					return "Code <-> Preview";
				case "Off":
					return "Off";
				default:
					return "Code -> Preview";
			}
		}

		private static string BuildIndividualExportScript()
		{
			return @"<script>
window.exportIndividualDiagrams=function(format){
	var diagrams=Array.prototype.slice.call(document.querySelectorAll('#preview .mermaid')).slice(0,99);
	if(diagrams.length===0){chrome.webview.postMessage('export-data:'+format+'-individual:NO_DIAGRAM');return;}
	var results=[];
	var index=0;
	function fail(){chrome.webview.postMessage('export-data:'+format+'-individual:SVG_RENDER_FAILED');}
	function serialize(svg){
		var standalone=svg.cloneNode(true);
		if(!standalone.getAttribute('xmlns'))standalone.setAttribute('xmlns','http://www.w3.org/2000/svg');
		return new XMLSerializer().serializeToString(standalone);
	}
	function finish(){chrome.webview.postMessage('export-data:'+format+'-individual:'+results.join(';'));}
	function renderNext(){
		if(index>=diagrams.length){finish();return;}
		var svg=diagrams[index].querySelector('svg');
		if(!svg){fail();return;}
		var serialized=serialize(svg);
		if(format==='svg'){
			results.push(btoa(unescape(encodeURIComponent(serialized))));
			index++;
			renderNext();
			return;
		}
		var image=new Image();
		image.onload=function(){
			var bounds=svg.getBoundingClientRect();
			var scale=2;
			var canvas=document.createElement('canvas');
			canvas.width=Math.max(1,Math.ceil(bounds.width))*scale;
			canvas.height=Math.max(1,Math.ceil(bounds.height))*scale;
			var context=canvas.getContext('2d');
			context.fillStyle=document.body.classList.contains('dark')?'#202124':'#ffffff';
			context.fillRect(0,0,canvas.width,canvas.height);
			context.drawImage(image,0,0,canvas.width,canvas.height);
			results.push(canvas.toDataURL('image/png').split(',')[1]);
			index++;
			renderNext();
		};
		image.onerror=fail;
		image.src='data:image/svg+xml;charset=utf-8,'+encodeURIComponent(serialized);
	}
	renderNext();
};
</script>";
		}

		private static string BuildHtmlExportScript()
		{
			return @"<script>
window.exportPreviewHtml=function(){
	var preview=document.getElementById('preview');
	var exportedPreview=preview.cloneNode(true);
	var diagrams=exportedPreview.querySelectorAll('.mermaid');
	for(var i=0;i<diagrams.length;i++){
		var sourceSvg=diagrams[i].querySelector('svg');
		if(!sourceSvg){chrome.webview.postMessage('export-data:html:SVG_RENDER_FAILED');return;}
		var svg=sourceSvg.cloneNode(true);
		if(!svg.getAttribute('xmlns'))svg.setAttribute('xmlns','http://www.w3.org/2000/svg');
		svg.classList.add('mermaid-svg');
		diagrams[i].parentNode.replaceChild(svg,diagrams[i]);
	}
	var includeOutline=document.getElementById('export-with-outline').checked;
	var outlineHtml='';
	if(includeOutline){
		var outline=document.createElement('aside');
		outline.id='outline';
		var nav=document.createElement('nav');
		var headings=exportedPreview.querySelectorAll('h1,h2,h3,h4,h5,h6');
		headings.forEach(function(heading,index){
			var label=heading.textContent.replace(/\s+/g,' ').trim();
			if(!label)return;
			var id=heading.id||'export-outline-'+index;
			heading.id=id;
			var link=document.createElement('a');
			link.href='#'+id;
			link.textContent=label;
			link.style.paddingLeft=(8+(parseInt(heading.tagName.substring(1),10)-1)*12)+'px';
			nav.appendChild(link);
		});
		if(!nav.childNodes.length)nav.textContent='No outline available.';
		outline.appendChild(nav);
		outlineHtml=outline.outerHTML;
	}
	var outlineStyles=includeOutline?'body.with-outline{margin:0}body.with-outline #outline{position:fixed;top:0;bottom:0;left:0;width:280px;padding:16px;overflow:auto;background:#f5f5f5;border-right:1px solid #dadce0}body.dark.with-outline #outline{background:#303134;border-color:#5f6368}body.with-outline #outline nav{display:flex;flex-direction:column}body.with-outline #outline a{padding-top:3px;padding-bottom:3px;color:inherit;text-decoration:none}body.with-outline #outline a:hover{text-decoration:underline}body.with-outline #preview{min-height:100vh;margin-left:280px;padding:16px}':'';
	var bodyClass=(document.body.classList.contains('dark')?'dark':'')+(includeOutline?' with-outline':'');
	var bodyContent=includeOutline?outlineHtml+'<main id=""preview"">'+exportedPreview.innerHTML+'</main>':exportedPreview.innerHTML;
	var html='<!doctype html><html><head><meta charset=""utf-8""><style>*{box-sizing:border-box}body{font-family:Segoe UI,Meiryo,sans-serif;margin:16px;color:#202124;background:#fff}body.dark{color:#e8eaed;background:#202124}pre{background:#f5f5f5;padding:12px;overflow:auto}body.dark pre{background:#303134}code{font-family:Consolas,monospace}table{border-collapse:collapse}th,td{border:1px solid #aaa;padding:4px 8px}.mermaid-svg{display:block;max-width:100%;margin:16px 0}'+outlineStyles+'</style></head><body'+(bodyClass?' class=""'+bodyClass+'""':'')+'>'+bodyContent+'</body></html>';
	chrome.webview.postMessage('export-data:html:'+btoa(unescape(encodeURIComponent(html))));
};
</script>";
		}

		private static string GetOutlineIconPath(string fileName)
		{
			string assemblyDirectory = Path.GetDirectoryName(typeof(MermaidViewerForm).Assembly.Location);
			string[] candidates =
			{
				assemblyDirectory == null ? null : Path.Combine(assemblyDirectory, "Assets", fileName),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName)
			};

			foreach (string candidate in candidates)
			{
				if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
					return candidate;
			}

			throw new FileNotFoundException("The outline icon could not be found.", candidates[0]);
		}

		private static string EscapeJavaScriptString(string value)
		{
			return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
		}

		private static string GetHostFilePath()
		{
			string renderDirectory = Path.Combine(GetWebViewUserDataFolder(), "render");
			Directory.CreateDirectory(renderDirectory);
			return Path.Combine(renderDirectory, "host.html");
		}

		private static string GetMermaidScriptPath()
		{
			string assemblyDirectory = Path.GetDirectoryName(typeof(MermaidViewerForm).Assembly.Location);
			string[] candidates =
			{
				assemblyDirectory == null ? null : Path.Combine(assemblyDirectory, "Assets", "mermaid.min.js"),
				assemblyDirectory == null ? null : Path.Combine(assemblyDirectory, "mermaid.min.js"),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "mermaid.min.js"),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mermaid.min.js")
			};

			foreach (string candidate in candidates)
			{
				if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
					return candidate;
			}

			return null;
		}

		private static string GetWebViewUserDataFolder()
		{
			string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (string.IsNullOrEmpty(localApplicationData))
				throw new InvalidOperationException("Could not retrieve the WebView2 user data folder.");
			return Path.Combine(localApplicationData, "NppMermaidViewer", "WebView2");
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				browser.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
		