using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.Win32;
using Kbg.NppPluginNET.PluginInfrastructure;
using NppDemo.Utils;
using NppMermaidViewer;
using NppMermaidViewer.Utils;

namespace Kbg.NppPluginNET
{
	internal static class Main
	{
		internal const string PluginName = "MermaidViewer";
		internal static readonly string PluginConfigDirectory = Path.Combine(Npp.notepad.GetConfigDirectory(), PluginName);
		private const int ToggleCommandId = 0;
		private const int AutoPreviewCommandId = 1;
		private const int FileExtensionsCommandId = 2;
		private const int SeparatorCommandId = 3;
		private const int ShowOutlineCommandId = 4;
		private const int HideOutlineCommandId = 5;
		private const int OutlineSeparatorCommandId = 6;
		private const int SyncCodeToPreviewCommandId = 7;
		private const int SyncPreviewToCodeCommandId = 8;
		private const int SyncCodeAndPreviewCommandId = 9;
		private const int SyncScrollOffCommandId = 10;
		private const int ColorSyncSeparatorCommandId = 11;
		private const int AutoColorModeCommandId = 12;
		private const int LightColorModeCommandId = 13;
		private const int DarkColorModeCommandId = 14;
		private const int ExportHtmlSeparatorCommandId = 15;
		private const int ExportHtmlCommandId = 16;
		private const int ExportHtmlWithOutlineCommandId = 17;
		private const int ExportImageSeparatorCommandId = 18;
		private const int ExportPngCommandId = 19;
		private const int ExportSvgCommandId = 20;
		private const int LicenseSeparatorCommandId = 21;
		private const int LicenseCommandId = 22;
		private const string LicenseFileName = "LICENSE.md";
		private const string ColorModeFileName = "ColorMode.txt";
		private const string AutoPreviewSettingsFileName = "AutoPreviewSettings.txt";
		private const string DefaultFileExtensions = "md,mmd,txt";
		private const uint MenuByPosition = 0x400;
		private const uint MenuPopup = 0x10;
		private const uint MenuChecked = 0x8;
		private enum ColorMode { Auto, Light, Dark }
		private enum ScrollSyncMode { CodeToPreview, PreviewToCode, CodeAndPreview, Off }
		private static MermaidViewerForm viewer;
		private static bool previewVisible;
		private static bool autoPreviewEnabled;
		private static string autoPreviewExtensions = DefaultFileExtensions;
		private static bool previewShownAutomatically;
		private static bool outlineVisible;
		private static ScrollSyncMode scrollSyncMode = ScrollSyncMode.CodeToPreview;
		private static int lastSyncedSourceLine = -1;
		private static bool syncingCodeFromPreview;
		private static ColorMode colorMode = ColorMode.Auto;
		private static IntPtr colorModeSubmenuHandle;
		private static bool colorModeMenuConfigured;
		private static Icon toolbarIcon;
		private static IntPtr toolbarBitmap;
		private const int ToolbarIconSize = 128;
		private const int RenderDebounceMilliseconds = 300;
		private static CancellationTokenSource pendingRenderCancellation;
		private static bool assemblyResolverRegistered;
		private static bool systemEventsRegistered;

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr handle);

		[DllImport("user32.dll")]
		private static extern IntPtr CreatePopupMenu();

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr newItem, string itemText);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, IntPtr newItem, string itemText);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool RemoveMenu(IntPtr menu, uint position, uint flags);

		[DllImport("user32.dll")]
		private static extern int GetMenuItemCount(IntPtr menu);

		[DllImport("user32.dll")]
		private static extern IntPtr GetSubMenu(IntPtr menu, int position);

		[DllImport("user32.dll")]
		private static extern uint GetMenuItemID(IntPtr menu, int position);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int maxCount, uint flags);

		[DllImport("user32.dll")]
		private static extern bool DrawMenuBar(IntPtr window);

		[DllImport("user32.dll")]
		private static extern bool DestroyMenu(IntPtr menu);

		internal static void CommandMenuInit()
		{
			RegisterPluginAssemblyResolver();
			LoadColorMode();
			LoadAutoPreviewSettings();
			if (!systemEventsRegistered)
			{
				SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
				systemEventsRegistered = true;
			}
			PluginBase.SetCommand(ToggleCommandId, "&Toggle Mermaid Preview", TogglePreview, previewVisible);
			PluginBase.SetCommand(AutoPreviewCommandId, "Automatic preview", ToggleAutoPreview, autoPreviewEnabled);
			PluginBase.SetCommand(FileExtensionsCommandId, "File extensions", ConfigureFileExtensions);
			PluginBase.SetCommand(SeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(ShowOutlineCommandId, "Show outline", ShowOutline, outlineVisible);
			PluginBase.SetCommand(HideOutlineCommandId, "Hide outline", HideOutline, !outlineVisible);
			PluginBase.SetCommand(OutlineSeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(SyncCodeToPreviewCommandId, "Scroll sync: Code -> Preview", () => SetScrollSyncMode(ScrollSyncMode.CodeToPreview), scrollSyncMode == ScrollSyncMode.CodeToPreview);
			PluginBase.SetCommand(SyncPreviewToCodeCommandId, "Scroll sync: Preview -> Code", () => SetScrollSyncMode(ScrollSyncMode.PreviewToCode), scrollSyncMode == ScrollSyncMode.PreviewToCode);
			PluginBase.SetCommand(SyncCodeAndPreviewCommandId, "Scroll sync: Both directions", () => SetScrollSyncMode(ScrollSyncMode.CodeAndPreview), scrollSyncMode == ScrollSyncMode.CodeAndPreview);
			PluginBase.SetCommand(SyncScrollOffCommandId, "Scroll sync: Off", () => SetScrollSyncMode(ScrollSyncMode.Off), scrollSyncMode == ScrollSyncMode.Off);
			PluginBase.SetCommand(ColorSyncSeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(AutoColorModeCommandId, "Color mode: Auto", () => SetColorMode(ColorMode.Auto), colorMode == ColorMode.Auto);
			PluginBase.SetCommand(LightColorModeCommandId, "Color mode: Light", () => SetColorMode(ColorMode.Light), colorMode == ColorMode.Light);
			PluginBase.SetCommand(DarkColorModeCommandId, "Color mode: Dark", () => SetColorMode(ColorMode.Dark), colorMode == ColorMode.Dark);
			PluginBase.SetCommand(ExportHtmlSeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(ExportHtmlCommandId, "Export HTML", ExportHtml);
			PluginBase.SetCommand(ExportHtmlWithOutlineCommandId, "Export HTML with outline", ExportHtmlWithOutline);
			PluginBase.SetCommand(ExportImageSeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(ExportPngCommandId, "Export Mermaid diagrams as PNG", ExportPng);
			PluginBase.SetCommand(ExportSvgCommandId, "Export Mermaid diagrams as SVG", ExportSvg);
			PluginBase.SetCommand(LicenseSeparatorCommandId, string.Empty, null);
			PluginBase.SetCommand(LicenseCommandId, "License", ShowLicense);
		}

		private static void LoadAutoPreviewSettings()
		{
			string path = Path.Combine(PluginConfigDirectory, AutoPreviewSettingsFileName);
			if (!File.Exists(path))
				return;

			try
			{
				string[] settings = File.ReadAllLines(path);
				bool enabled;
				if (settings.Length > 0 && bool.TryParse(settings[0], out enabled))
					autoPreviewEnabled = enabled;
				if (settings.Length > 1)
					autoPreviewExtensions = settings[1];
			}
			catch (Exception exception)
			{
				Logger.LogException("Main.LoadAutoPreviewSettings", exception);
			}
		}

		private static bool SaveAutoPreviewSettings()
		{
			try
			{
				Directory.CreateDirectory(PluginConfigDirectory);
				File.WriteAllLines(Path.Combine(PluginConfigDirectory, AutoPreviewSettingsFileName), new[]
				{
					autoPreviewEnabled.ToString(),
					autoPreviewExtensions
				});
				return true;
			}
			catch (Exception exception)
			{
				Logger.LogException("Main.SaveAutoPreviewSettings", exception);
				MessageBox.Show("Failed to save auto-preview settings." + Environment.NewLine + exception.Message,
					PluginName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return false;
			}
		}

		private static void ConfigureFileExtensions()
		{
			using (Form dialog = new Form())
			using (TextBox extensionsTextBox = new TextBox())
			using (Button okButton = new Button())
			using (Button cancelButton = new Button())
			{
				dialog.Text = "File extensions";
				dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
				dialog.StartPosition = FormStartPosition.CenterScreen;
				dialog.ShowInTaskbar = false;
				dialog.MinimizeBox = false;
				dialog.MaximizeBox = false;
				dialog.ClientSize = new Size(360, 105);

				Label label = new Label
				{
					Text = "File extensions (comma-separated):",
					AutoSize = true,
					Left = 12,
					Top = 12
				};
				extensionsTextBox.Left = 12;
				extensionsTextBox.Top = 34;
				extensionsTextBox.Width = 336;
				extensionsTextBox.Text = autoPreviewExtensions;

				okButton.Text = "OK";
				okButton.DialogResult = DialogResult.OK;
				okButton.Left = 192;
				okButton.Top = 68;
				okButton.Width = 75;
				cancelButton.Text = "Cancel";
				cancelButton.DialogResult = DialogResult.Cancel;
				cancelButton.Left = 273;
				cancelButton.Top = 68;
				cancelButton.Width = 75;
				dialog.AcceptButton = okButton;
				dialog.CancelButton = cancelButton;
				dialog.Controls.AddRange(new Control[] { label, extensionsTextBox, okButton, cancelButton });

				if (dialog.ShowDialog() == DialogResult.OK)
				{
					string previousExtensions = autoPreviewExtensions;
					autoPreviewExtensions = extensionsTextBox.Text.Trim();
					if (SaveAutoPreviewSettings())
						UpdateAutoPreviewForCurrentDocument();
					else
						autoPreviewExtensions = previousExtensions;
				}
			}
		}
		private static void ShowOutline()
		{
			outlineVisible = true;
			UpdateOutlineMenuChecks();
			if (viewer != null && !viewer.IsDisposed)
				_ = viewer.SetOutlineVisibleAsync(true);
		}

		private static void HideOutline()
		{
			outlineVisible = false;
			UpdateOutlineMenuChecks();
			if (viewer != null && !viewer.IsDisposed)
				_ = viewer.SetOutlineVisibleAsync(false);
		}

		private static void UpdateOutlineMenuChecks()
		{
			PluginBase.CheckMenuItem(ShowOutlineCommandId, outlineVisible);
			PluginBase.CheckMenuItem(HideOutlineCommandId, !outlineVisible);
		}

		private static void OnOutlineVisibilityChanged(bool visible)
		{
			outlineVisible = visible;
			UpdateOutlineMenuChecks();
		}

		private static void OnThemeChangeRequested(bool darkTheme)
		{
			SetColorMode(darkTheme ? ColorMode.Dark : ColorMode.Light);
		}

		private static void ExportHtml() => ExportFromPlugin("html", false);

		private static void ExportHtmlWithOutline() => ExportFromPlugin("html", true);

		private static void ExportPng() => ExportFromPlugin("png-individual", false);

		private static void ExportSvg() => ExportFromPlugin("svg-individual", false);

		private static async void ExportFromPlugin(string format, bool includeOutline)
		{
			if (!previewVisible)
				TogglePreview();

			if (viewer == null || viewer.IsDisposed)
				return;

			await RenderCurrentDocumentAsync();
			await viewer.ExportAsync(format, includeOutline);
		}

		private static void SetScrollSyncMode(ScrollSyncMode mode)
		{
			scrollSyncMode = mode;
			PluginBase.CheckMenuItem(SyncCodeToPreviewCommandId, mode == ScrollSyncMode.CodeToPreview);
			PluginBase.CheckMenuItem(SyncPreviewToCodeCommandId, mode == ScrollSyncMode.PreviewToCode);
			PluginBase.CheckMenuItem(SyncCodeAndPreviewCommandId, mode == ScrollSyncMode.CodeAndPreview);
			PluginBase.CheckMenuItem(SyncScrollOffCommandId, mode == ScrollSyncMode.Off);
			if (viewer != null && !viewer.IsDisposed)
				_ = viewer.SetSyncScrollModeAsync(mode.ToString());
			if (mode == ScrollSyncMode.CodeToPreview || mode == ScrollSyncMode.CodeAndPreview)
			{
				lastSyncedSourceLine = -1;
				SyncPreviewScrollFromEditor();
			}
		}

		private static void SyncPreviewScrollFromEditor()
		{
			if ((scrollSyncMode != ScrollSyncMode.CodeToPreview && scrollSyncMode != ScrollSyncMode.CodeAndPreview) || viewer == null || viewer.IsDisposed || Npp.editor == null)
				return;

			int firstVisibleLine = Npp.editor.GetFirstVisibleLine();
			if (firstVisibleLine == lastSyncedSourceLine)
				return;
			lastSyncedSourceLine = firstVisibleLine;

			int lineCount = Npp.editor.GetLineCount();
			double scrollRatio = lineCount <= 1 ? 0 : (double)firstVisibleLine / (lineCount - 1);
			_ = viewer.SyncPreviewScrollAsync(scrollRatio);
		}

		private static void OnSyncScrollModeChangeRequested()
		{
			ScrollSyncMode nextMode;
			switch (scrollSyncMode)
			{
				case ScrollSyncMode.CodeToPreview:
					nextMode = ScrollSyncMode.PreviewToCode;
					break;
				case ScrollSyncMode.PreviewToCode:
					nextMode = ScrollSyncMode.CodeAndPreview;
					break;
				case ScrollSyncMode.CodeAndPreview:
					nextMode = ScrollSyncMode.Off;
					break;
				default:
					nextMode = ScrollSyncMode.CodeToPreview;
					break;
			}
			SetScrollSyncMode(nextMode);
		}

		private static void OnPreviewScrollRequested(double scrollRatio)
		{
			if ((scrollSyncMode != ScrollSyncMode.PreviewToCode && scrollSyncMode != ScrollSyncMode.CodeAndPreview) || Npp.editor == null)
				return;

			int lineCount = Npp.editor.GetLineCount();
			if (lineCount <= 1)
				return;

			int targetLine = (int)Math.Round(Math.Max(0, Math.Min(1, scrollRatio)) * (lineCount - 1));
			if (targetLine != Npp.editor.GetFirstVisibleLine())
			{
				syncingCodeFromPreview = true;
				try
				{
					Npp.editor.SetFirstVisibleLine(targetLine);
				}
				finally
				{
					syncingCodeFromPreview = false;
				}
			}
		}

		private static void LoadColorMode()
		{
			string path = Path.Combine(PluginConfigDirectory, ColorModeFileName);
			if (!File.Exists(path))
				return;

			ColorMode loadedMode;
			if (Enum.TryParse(File.ReadAllText(path).Trim(), true, out loadedMode) && Enum.IsDefined(typeof(ColorMode), loadedMode))
				colorMode = loadedMode;
		}

		private static void SetColorMode(ColorMode mode)
		{
			colorMode = mode;
			if (colorModeSubmenuHandle == IntPtr.Zero)
			{
				PluginBase.CheckMenuItem(AutoColorModeCommandId, colorMode == ColorMode.Auto);
				PluginBase.CheckMenuItem(LightColorModeCommandId, colorMode == ColorMode.Light);
				PluginBase.CheckMenuItem(DarkColorModeCommandId, colorMode == ColorMode.Dark);
			}
			else
			{
				Win32.CheckMenuItem(colorModeSubmenuHandle, AutoColorModeCommandId, Win32.MF_BYCOMMAND | (colorMode == ColorMode.Auto ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
				Win32.CheckMenuItem(colorModeSubmenuHandle, LightColorModeCommandId, Win32.MF_BYCOMMAND | (colorMode == ColorMode.Light ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
				Win32.CheckMenuItem(colorModeSubmenuHandle, DarkColorModeCommandId, Win32.MF_BYCOMMAND | (colorMode == ColorMode.Dark ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
			}

			Directory.CreateDirectory(PluginConfigDirectory);
			File.WriteAllText(Path.Combine(PluginConfigDirectory, ColorModeFileName), colorMode.ToString());
			_ = RenderCurrentDocumentAsync();
		}

		internal static void ConfigureColorModeSubmenu()
		{
			if (colorModeMenuConfigured)
				return;
			try
			{
				ConfigureColorModeSubmenuCore();
			}
			catch (Exception exception)
			{
				Logger.LogException("Main.ConfigureColorModeSubmenu", exception);
			}
		}

		private static void ConfigureColorModeSubmenuCore()
		{
			IntPtr pluginsMenu = Win32.SendMessage(PluginBase.nppData._nppHandle,
				(uint)NppMsg.NPPM_GETMENUHANDLE, (int)NppMsg.NPPPLUGINMENU, IntPtr.Zero);
			IntPtr pluginMenu = FindSubmenuByText(pluginsMenu, PluginName);
			IntPtr mainMenu = Win32.GetMenu(PluginBase.nppData._nppHandle);
			if (pluginMenu == IntPtr.Zero)
				pluginMenu = FindSubmenuByText(mainMenu, PluginName);
			if (pluginMenu == IntPtr.Zero && HasColorModeCommands(pluginsMenu))
				pluginMenu = pluginsMenu;
			if (pluginMenu == IntPtr.Zero && HasColorModeCommands(mainMenu))
				pluginMenu = mainMenu;
			if (!HasColorModeCommands(pluginMenu))
				return;

			int autoPosition = FindMenuCommandPosition(pluginMenu, AutoColorModeCommandId);
			int lightPosition = FindMenuCommandPosition(pluginMenu, LightColorModeCommandId);
			int darkPosition = FindMenuCommandPosition(pluginMenu, DarkColorModeCommandId);
			if (autoPosition < 0 || lightPosition < 0 || darkPosition < 0)
				return;

			IntPtr submenu = CreatePopupMenu();
			if (submenu == IntPtr.Zero)
				return;

			if (!AppendMenu(submenu, MenuCheckedIf(ColorMode.Auto), new IntPtr(AutoColorModeCommandId), "Auto") ||
				!AppendMenu(submenu, MenuCheckedIf(ColorMode.Light), new IntPtr(LightColorModeCommandId), "Light") ||
				!AppendMenu(submenu, MenuCheckedIf(ColorMode.Dark), new IntPtr(DarkColorModeCommandId), "Dark"))
			{
				DestroyMenu(submenu);
				return;
			}

			if (!InsertMenu(pluginMenu, (uint)autoPosition, MenuByPosition | MenuPopup, submenu, "Color mode"))
			{
				DestroyMenu(submenu);
				return;
			}

			colorModeSubmenuHandle = submenu;
			RemoveMenuCommand(pluginMenu, DarkColorModeCommandId);
			RemoveMenuCommand(pluginMenu, LightColorModeCommandId);
			RemoveMenuCommand(pluginMenu, AutoColorModeCommandId);
			DrawMenuBar(PluginBase.nppData._nppHandle);
			colorModeMenuConfigured = true;
		}

		private static uint MenuCheckedIf(ColorMode mode)
		{
			return colorMode == mode ? MenuChecked : 0;
		}

		private static bool RemoveMenuCommand(IntPtr menu, int commandId)
		{
			int position = FindMenuCommandPosition(menu, commandId);
			return position >= 0 && RemoveMenu(menu, (uint)position, MenuByPosition);
		}

		private static int FindMenuCommandPosition(IntPtr menu, int commandId)
		{
			int count = GetMenuItemCount(menu);
			for (int position = 0; position < count; position++)
			{
				if (GetMenuItemID(menu, position) == (uint)commandId)
					return position;
			}
			return -1;
		}

		private static bool HasColorModeCommands(IntPtr menu)
		{
			return menu != IntPtr.Zero &&
				FindMenuCommandPosition(menu, AutoColorModeCommandId) >= 0 &&
				FindMenuCommandPosition(menu, LightColorModeCommandId) >= 0 &&
				FindMenuCommandPosition(menu, DarkColorModeCommandId) >= 0;
		}

		private static IntPtr FindSubmenuByText(IntPtr menu, string title)
		{
			if (menu == IntPtr.Zero)
				return IntPtr.Zero;

			int count = GetMenuItemCount(menu);
			for (int position = 0; position < count; position++)
			{
				IntPtr submenu = GetSubMenu(menu, position);
				if (submenu == IntPtr.Zero)
					continue;

				System.Text.StringBuilder text = new System.Text.StringBuilder(128);
				GetMenuString(menu, (uint)position, text, text.Capacity, MenuByPosition);
				if (string.Equals(text.ToString().Replace("&", string.Empty).Trim(), title, StringComparison.OrdinalIgnoreCase))
					return submenu;

				IntPtr nestedMenu = FindSubmenuByText(submenu, title);
				if (nestedMenu != IntPtr.Zero)
					return nestedMenu;
			}
			return IntPtr.Zero;
		}

		private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
		{
			if ((e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.General) ||
				colorMode != ColorMode.Auto || !previewVisible || viewer == null || viewer.IsDisposed || !viewer.IsHandleCreated)
				return;

			try
			{
				viewer.BeginInvoke((Action)(() => _ = RenderCurrentDocumentAsync()));
			}
			catch (InvalidOperationException)
			{
			}
		}

		private static string GetMermaidTheme()
		{
			bool darkMode = colorMode == ColorMode.Dark || (colorMode == ColorMode.Auto && IsWindowsDarkMode());
			return darkMode ? "dark" : "default";
		}

		private static bool IsWindowsDarkMode()
		{
			try
			{
				using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
				{
					if (key != null)
					{
						bool systemIsLight;
						bool appsAreLight;
						bool hasSystemValue = TryGetLightThemeSetting(key, "SystemUsesLightTheme", out systemIsLight);
						bool hasAppsValue = TryGetLightThemeSetting(key, "AppsUseLightTheme", out appsAreLight);
						if (hasSystemValue && hasAppsValue && systemIsLight != appsAreLight)
							return !appsAreLight;
						if (hasSystemValue)
							return !systemIsLight;
						if (hasAppsValue)
							return !appsAreLight;
					}
				}
			}
			catch (Exception exception)
			{
				Logger.LogException("Main.IsWindowsDarkMode", exception);
			}

			return Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETDARKMODE, 0, 0).ToInt32() != 0;
		}

		private static bool TryGetLightThemeSetting(RegistryKey key, string name, out bool isLight)
		{
			object value = key.GetValue(name);
			if (value is int)
			{
				isLight = (int)value != 0;
				return true;
			}

			isLight = false;
			return false;
		}

		private static void RegisterPluginAssemblyResolver()
		{
			if (assemblyResolverRegistered)
				return;
			AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;
			assemblyResolverRegistered = true;
		}

		private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args)
		{
			AssemblyName requestedAssembly = new AssemblyName(args.Name);
			if (!requestedAssembly.Name.StartsWith("Microsoft.Web.WebView2.", StringComparison.Ordinal) &&
				!string.Equals(requestedAssembly.Name, "Markdig", StringComparison.Ordinal) &&
				!requestedAssembly.Name.StartsWith("Microsoft.WindowsAPICodePack", StringComparison.Ordinal))
				return null;

			string assemblyDirectory = Path.GetDirectoryName(typeof(Main).Assembly.Location);
			if (string.IsNullOrEmpty(assemblyDirectory))
				return null;
			string candidatePath = Path.Combine(assemblyDirectory, requestedAssembly.Name + ".dll");
			if (!File.Exists(candidatePath))
				return null;

			try
			{
				AssemblyName candidateAssembly = AssemblyName.GetAssemblyName(candidatePath);
				return string.Equals(candidateAssembly.FullName, requestedAssembly.FullName, StringComparison.Ordinal)
					? Assembly.LoadFrom(candidatePath)
					: null;
			}
			catch (BadImageFormatException)
			{
				return null;
			}
			catch (FileLoadException)
			{
				return null;
			}
		}

		internal static void SetToolBarIcons()
		{
			if (toolbarIcon == null)
			{
				string iconPath = GetIconPath();
				if (iconPath == null)
					throw new FileNotFoundException("The toolbar icon could not be found.", iconPath);
				using (Bitmap source = new Bitmap(iconPath))
				using (Bitmap bitmap = new Bitmap(ToolbarIconSize, ToolbarIconSize))
				{
					using (Graphics graphics = Graphics.FromImage(bitmap))
						graphics.DrawImage(source, 0, 0, ToolbarIconSize, ToolbarIconSize);
					toolbarIcon = Icon.FromHandle(bitmap.GetHicon());
					toolbarBitmap = bitmap.GetHbitmap();
				}
			}
			toolbarIcons icons = new toolbarIcons
			{
				hToolbarBmp = toolbarBitmap,
				hToolbarIcon = toolbarIcon.Handle,
				hToolbarIconDarkMode = toolbarIcon.Handle
			};
			IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(toolbarIcons)));
			try
			{
				Marshal.StructureToPtr(icons, pointer, false);
				Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON_FORDARKMODE,
					PluginBase._funcItems.Items[ToggleCommandId]._cmdID, pointer);
			}
			finally
			{
				Marshal.FreeHGlobal(pointer);
			}
		}

		private static string GetIconPath()
		{
			string assemblyDirectory = Path.GetDirectoryName(typeof(Main).Assembly.Location);
			string[] candidates =
			{
				assemblyDirectory == null ? null : Path.Combine(assemblyDirectory, "Assets", "icon_t.png"),
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon_t.png")
			};

			foreach (string candidate in candidates)
			{
				if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
					return candidate;
			}

			return null;
		}

		private static void TogglePreview()
		{
			previewShownAutomatically = false;
			SetPreviewVisible(!previewVisible);
		}

		private static void SetPreviewVisible(bool visible)
		{
			if (previewVisible == visible)
				return;

			previewVisible = visible;
			PluginBase.CheckMenuItem(ToggleCommandId, previewVisible);
			if (previewVisible)
			{
				if (viewer == null || viewer.IsDisposed)
				{
					viewer = new MermaidViewerForm();
					_ = viewer.SetOutlineVisibleAsync(outlineVisible);
					_ = viewer.SetSyncScrollModeAsync(scrollSyncMode.ToString());
					viewer.ScrollSyncRequested += OnPreviewScrollRequested;
					viewer.SyncScrollModeChangeRequested += OnSyncScrollModeChangeRequested;
					viewer.OutlineVisibilityChanged += OnOutlineVisibilityChanged;
					viewer.ThemeChangeRequested += OnThemeChangeRequested;
					RegisterDockingForm(viewer);
				}
				Npp.notepad.ShowDockingForm(viewer);
				_ = RenderCurrentDocumentAsync();
			}
			else if (viewer != null && !viewer.IsDisposed)
			{
				Npp.notepad.HideDockingForm(viewer);
			}
		}

		private static void ToggleAutoPreview()
		{
			bool previousValue = autoPreviewEnabled;
			autoPreviewEnabled = !autoPreviewEnabled;
			if (!SaveAutoPreviewSettings())
			{
				autoPreviewEnabled = previousValue;
				return;
			}

			PluginBase.CheckMenuItem(AutoPreviewCommandId, autoPreviewEnabled);
			UpdateAutoPreviewForCurrentDocument();
		}

		private static void UpdateAutoPreviewForCurrentDocument()
		{
			if (!autoPreviewEnabled)
				return;

			string filePath = Npp.GetCurrentPath();
			string extension = Path.GetExtension(filePath);
			if (!string.IsNullOrEmpty(extension))
				extension = extension.TrimStart('.');

			bool extensionMatches = false;
			foreach (string configuredExtension in autoPreviewExtensions.Split(','))
			{
				string candidate = configuredExtension.Trim().TrimStart('.');
				if (candidate.Length > 0 && string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
				{
					extensionMatches = true;
					break;
				}
			}

			if (extensionMatches)
			{
				if (!previewVisible)
				{
					previewShownAutomatically = true;
					SetPreviewVisible(true);
				}
			}
			else if (previewShownAutomatically)
			{
				previewShownAutomatically = false;
				SetPreviewVisible(false);
			}
		}

		private static void ShowLicense()
		{
			string assemblyDirectory = Path.GetDirectoryName(typeof(Main).Assembly.Location);
			string licensePath = string.IsNullOrEmpty(assemblyDirectory)
				? null
				: Path.Combine(assemblyDirectory, LicenseFileName);

			if (string.IsNullOrEmpty(licensePath) || !File.Exists(licensePath))
			{
				MessageBox.Show("The license file could not be found.", PluginName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}

			if (!Npp.notepad.OpenFile(licensePath))
				MessageBox.Show("The license file could not be opened.", PluginName, MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		private static void RegisterDockingForm(MermaidViewerForm form)
		{
			NppTbData data = new NppTbData
			{
				hClient = form.Handle,
				pszName = form.Text,
				dlgID = ToggleCommandId,
				uMask = NppTbMsg.DWS_DF_CONT_RIGHT | NppTbMsg.DWS_ICONTAB,
				hIconTab = (uint)toolbarIcon.Handle,
				pszModuleName = PluginName
			};
			IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NppTbData)));
			try
			{
				Marshal.StructureToPtr(data, pointer, false);
				Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_DMMREGASDCKDLG, IntPtr.Zero, pointer);
			}
			finally
			{
				Marshal.FreeHGlobal(pointer);
			}
		}

		private static async Task RenderCurrentDocumentAsync()
		{
			if (!previewVisible || viewer == null || viewer.IsDisposed)
				return;
			try
			{
				Npp.editor = new ScintillaGateway(PluginBase.GetCurrentScintilla());
				bool isMermaidFile = string.Equals(Path.GetExtension(Npp.GetCurrentPath()), ".mmd", StringComparison.OrdinalIgnoreCase);
				await viewer.RenderAsync(Npp.editor.GetText(), GetMermaidTheme(), isMermaidFile).ConfigureAwait(true);
				SyncPreviewScrollFromEditor();
			}
			catch (Exception exception)
			{
				Logger.LogException("Main.RenderCurrentDocumentAsync", exception);
				string errorMessage = string.Format("{0}\r\nHRESULT: 0x{1:X8}\r\n{2}\r\n\r\nDetailed log: {3}",
					exception.GetType().FullName, exception.HResult, exception.Message, Logger.LogFilePath);
				MessageBox.Show(errorMessage, PluginName, MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private static void QueueRenderCurrentDocument()
		{
			CancelPendingRender();
			CancellationTokenSource cancellation = new CancellationTokenSource();
			pendingRenderCancellation = cancellation;
			_ = RenderCurrentDocumentAfterDelayAsync(cancellation);
		}

		private static async Task RenderCurrentDocumentAfterDelayAsync(CancellationTokenSource cancellation)
		{
			try
			{
				await Task.Delay(RenderDebounceMilliseconds, cancellation.Token).ConfigureAwait(true);
				if (!cancellation.IsCancellationRequested)
					await RenderCurrentDocumentAsync().ConfigureAwait(true);
			}
			catch (OperationCanceledException)
			{
				// A subsequent edit notification made this pending render unnecessary.
			}
			finally
			{
				if (ReferenceEquals(pendingRenderCancellation, cancellation))
					pendingRenderCancellation = null;
				cancellation.Dispose();
			}
		}

		private static void CancelPendingRender()
		{
			if (pendingRenderCancellation != null)
				pendingRenderCancellation.Cancel();
		}

		internal static void OnNotification(ScNotification notification)
		{
			switch (notification.Header.Code)
			{
				case (uint)NppMsg.NPPN_READY:
					ConfigureColorModeSubmenu();
					UpdateAutoPreviewForCurrentDocument();
					break;
				case (uint)NppMsg.NPPN_BUFFERACTIVATED:
					CancelPendingRender();
					Npp.editor = new ScintillaGateway(PluginBase.GetCurrentScintilla());
					UpdateAutoPreviewForCurrentDocument();
					_ = RenderCurrentDocumentAsync();
					break;
				case (uint)SciMsg.SCN_UPDATEUI:
					if (!syncingCodeFromPreview &&
						(scrollSyncMode == ScrollSyncMode.CodeToPreview || scrollSyncMode == ScrollSyncMode.CodeAndPreview) &&
						notification.Header.hwndFrom == PluginBase.GetCurrentScintilla())
						SyncPreviewScrollFromEditor();
					break;
				case (uint)SciMsg.SCN_MODIFIED:
				case (uint)NppMsg.NPPN_GLOBALMODIFIED:
					QueueRenderCurrentDocument();
					break;
				case (uint)NppMsg.NPPN_DARKMODECHANGED:
					if (colorMode == ColorMode.Auto)
						_ = RenderCurrentDocumentAsync();
					break;
			}
		}

		internal static void PluginCleanUp()
		{
			if (systemEventsRegistered)
			{
				SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
				systemEventsRegistered = false;
			}
			CancelPendingRender();
			if (viewer != null && !viewer.IsDisposed)
				viewer.Close();
			viewer = null;
			toolbarIcon?.Dispose();
			toolbarIcon = null;
			if (toolbarBitmap != IntPtr.Zero)
			{
				DeleteObject(toolbarBitmap);
				toolbarBitmap = IntPtr.Zero;
			}
		}
	}
}






