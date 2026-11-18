using Blish_HUD;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static Blish_HUD.GameService;

namespace entrhopi.HomeTab
{
    [Export(typeof(Blish_HUD.Modules.Module))]
    public class Module : Blish_HUD.Modules.Module
    {

        private const string NewModulesFeedUrl = "https://pkgs.blishhud.com/new-modules.xml";
        private const string ModuleUpdatesFeedUrl = "https://pkgs.blishhud.com/module-updates.xml";
        private const string GuildWarsFeedUrl = "https://www.guildwars2.com/en/feed/";
        private const int TabStripHeight = 36;
        private const int TabWidth = 150;
        private const int QuadrantGap = 8;
        private const int MaxMenuEntries = 5;

        private static readonly Color ActiveTabColor = Color.FromNonPremultiplied(55, 55, 55, 255);

        private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
        private static readonly Regex HtmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private static readonly Logger Logger = Logger.GetLogger<Module>();

        #region Service Managers
        internal SettingsManager SettingsManager => this.ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => this.ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => this.ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => this.ModuleParameters.Gw2ApiManager;
        #endregion

        private WindowTab _moduleTab;

        private Texture2D _homeIcon;

        private readonly List<RssItem> _newModules = new List<RssItem>();
        private readonly List<RssItem> _moduleUpdates = new List<RssItem>();
        private readonly List<RssItem> _guildWarsNews = new List<RssItem>();

        internal string TabName = "Home";

        [ImportingConstructor]
        public Module([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) { }

        protected override void DefineSettings(SettingCollection settings)
        {

        }

        protected override void Initialize()
        {
            _homeIcon = ContentsManager.GetTexture("255369.png");
        }

        protected override async Task LoadAsync()
        {
            await Task.WhenAll(
                LoadFeed(NewModulesFeedUrl, _newModules),
                LoadFeed(ModuleUpdatesFeedUrl, _moduleUpdates),
                LoadFeed(GuildWarsFeedUrl, _guildWarsNews)).ConfigureAwait(false);
        }

        private static async Task LoadFeed(string url, List<RssItem> target)
        {
            try
            {
                using (var client = new WebClient())
                {
                    var xml = await client.DownloadStringTaskAsync(url).ConfigureAwait(false);
                    ParseFeed(xml, url, target);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to load RSS feed from {url}.", url);
            }
        }

        protected override void OnModuleLoaded(EventArgs e)
        {
            _moduleTab = Overlay.BlishHudWindow.AddTab(
                TabName,
                _homeIcon,
                () => new StaticPanelView(HomeTabView(Overlay.BlishHudWindow)),
                int.MinValue);

            // Base handler must be called
            base.OnModuleLoaded(e);
        }

        private static void ParseFeed(string xml, string url, List<RssItem> target)
        {
            var channel = XDocument.Parse(xml)?.Root?.Element("channel");
            if (channel == null)
            {
                Logger.Warn("RSS feed from {url} did not contain a channel.", url);
                return;
            }

            target.Clear();
            target.AddRange(channel.Elements("item").Select(item =>
            {
                var published = ElementValue(item, "pubDate");
                DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.None, out var publishedAt);

                return new RssItem()
                {
                    Title       = ElementValue(item, "title"),
                    Link        = ElementValue(item, "link"),
                    Published   = published,
                    PublishedAt = publishedAt,
                    Author      = ElementValue(item, DcNamespace + "creator"),
                    Description = ToPlainText(ElementValue(item, "description"))
                };
            }));
        }

        private static string ElementValue(XElement parent, XName name)
        {
            return parent.Element(name)?.Value.Trim() ?? string.Empty;
        }

        private static string ToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            var text = WebUtility.HtmlDecode(html);
            text = HtmlTagRegex.Replace(text, string.Empty);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            return text.Trim();
        }

        private Panel HomeTabView(WindowBase wndw)
        {
            var parentPanel = new Panel()
            {
                CanScroll = false,
                Size = wndw.ContentRegion.Size
            };

            // Top left: Guild Wars 2 news.
            var topLeftPanel = new Panel()
            {
                Parent = parentPanel
            };

            var guildWarsHeader = new Panel()
            {
                Parent          = topLeftPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = TabStripHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = guildWarsHeader,
                Location       = new Point(10, 5),
                Text           = "Guild Wars 2",
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var guildWarsContent = new Panel()
            {
                Parent           = topLeftPanel,
                Location         = new Point(0, TabStripHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            BuildFeedMenu(guildWarsContent, _guildWarsNews);

            // Bottom right: tabbed Blish HUD module feeds.
            var bottomRightPanel = new Panel()
            {
                Parent = parentPanel
            };

            var tabStrip = new FlowPanel()
            {
                Parent              = bottomRightPanel,
                Location            = Point.Zero,
                WidthSizingMode     = SizingMode.Fill,
                Height              = TabStripHeight,
                FlowDirection       = ControlFlowDirection.SingleLeftToRight,
                ControlPadding      = new Vector2(4, 0),
                OuterControlPadding = new Vector2(8, 4)
            };

            var newModulesTab = new Label()
            {
                Parent          = tabStrip,
                Text            = "New Modules",
                Size            = new Point(TabWidth, StandardButton.STANDARD_CONTROL_HEIGHT),
                TextColor       = Color.White,
                BackgroundColor = ActiveTabColor
            };

            var moduleUpdatesTab = new Label()
            {
                Parent          = tabStrip,
                Text            = "Module Updates",
                Size            = new Point(TabWidth, StandardButton.STANDARD_CONTROL_HEIGHT),
                TextColor       = Color.LightGray,
                BackgroundColor = Color.Transparent
            };

            var contentPanel = new Panel()
            {
                Parent           = bottomRightPanel,
                Location         = new Point(0, TabStripHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            var newModulesPanel = new Panel()
            {
                Parent           = contentPanel,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            var moduleUpdatesPanel = new Panel()
            {
                Parent           = contentPanel,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill,
                Visible          = false
            };

            newModulesTab.Click += (sender, args) => SelectTab(newModulesTab, newModulesPanel, moduleUpdatesTab, moduleUpdatesPanel);
            moduleUpdatesTab.Click += (sender, args) => SelectTab(moduleUpdatesTab, moduleUpdatesPanel, newModulesTab, newModulesPanel);

            BuildFeedMenu(newModulesPanel, _newModules);
            BuildFeedMenu(moduleUpdatesPanel, _moduleUpdates);

            parentPanel.Resized += (sender, args) => UpdateQuadrants(parentPanel, topLeftPanel, bottomRightPanel);
            UpdateQuadrants(parentPanel, topLeftPanel, bottomRightPanel);

            return parentPanel;
        }

        private static void UpdateQuadrants(Panel parent, Panel topLeft, Panel bottomRight)
        {
            var width  = parent.ContentRegion.Width;
            var height = parent.ContentRegion.Height;
            var quadrantWidth  = Math.Max(0, (width - QuadrantGap) / 2);
            var quadrantHeight = Math.Max(0, (height - QuadrantGap) / 2);
            var rightColumnX   = quadrantWidth + QuadrantGap;
            var bottomRowY     = quadrantHeight + QuadrantGap;

            topLeft.Location = Point.Zero;
            topLeft.Size     = new Point(quadrantWidth, quadrantHeight);

            bottomRight.Location = new Point(rightColumnX, bottomRowY);
            bottomRight.Size     = new Point(Math.Max(0, width - rightColumnX), Math.Max(0, height - bottomRowY));
        }

        private static void SelectTab(Label selectedTab, Panel selectedPanel, Label otherTab, Panel otherPanel)
        {
            selectedTab.BackgroundColor = ActiveTabColor;
            selectedTab.TextColor       = Color.White;
            otherTab.BackgroundColor    = Color.Transparent;
            otherTab.TextColor          = Color.LightGray;

            selectedPanel.Visible = true;
            otherPanel.Visible    = false;
        }

        private static void BuildFeedMenu(Panel parent, IEnumerable<RssItem> items)
        {
            var scrollPanel = new Panel()
            {
                Parent           = parent,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill,
                CanScroll        = true
            };

            var menu = new Menu()
            {
                Parent          = scrollPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                MenuItemHeight  = 36
            };

            foreach (var item in items
                .OrderByDescending(entry => entry.PublishedAt)
                .Take(MaxMenuEntries))
            {
                var menuItem = menu.AddMenuItem(item.Title);
                menuItem.BasicTooltipText = BuildTooltip(item);

                if (!string.IsNullOrWhiteSpace(item.Link))
                {
                    var link = item.Link;
                    menuItem.Click += (sender, args) => OpenLink(link);
                }
            }
        }

        private static void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to open link {url}.", url);
            }
        }

        private static string BuildTooltip(RssItem item)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Title)) lines.Add(item.Title);

            var meta = string.Join("  •  ", new[] { item.Published, item.Author }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(meta)) lines.Add(meta);

            if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add(item.Description);
            if (!string.IsNullOrWhiteSpace(item.Link)) lines.Add(item.Link);

            return string.Join("\n", lines);
        }

        protected override void Update(GameTime gameTime)
        {

        }

        /// <inheritdoc />
        protected override void Unload()
        {
            // Unload here

            // All static members must be manually unset
        }

        private sealed class RssItem
        {
            public string Title { get; set; }
            public string Link { get; set; }
            public string Published { get; set; }
            public DateTimeOffset PublishedAt { get; set; }
            public string Author { get; set; }
            public string Description { get; set; }
        }

    }

}
