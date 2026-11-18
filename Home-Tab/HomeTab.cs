using Blish_HUD;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
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
        private const string DailiesUrl = "https://api.guildwars2.com/v2/account/wizardsvault/daily";
        private const int HeadingHeight = 36;
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
        private readonly List<string> _dailies = new List<string>();
        private string _dailiesMessage = string.Empty;
        private Panel _dailiesPanel;
        private bool _dailiesDirty;
        private bool _dailiesNeedsRetry;
        private bool _dailiesLoading;
        private DateTime _nextDailiesAttempt = DateTime.MinValue;

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
                LoadFeed(GuildWarsFeedUrl, _guildWarsNews),
                LoadDailies()).ConfigureAwait(false);
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

        private async Task LoadDailies()
        {
            if (_dailiesLoading) return;

            _dailiesLoading = true;
            _dailies.Clear();

            try
            {
                if (!TryGetConfiguredApiKey(out var apiKey))
                {
                    _dailiesMessage = "Please configure an API key in the Blish HUD settings.";
                    _dailiesNeedsRetry = true;
                    return;
                }

                try
                {
                    using (var client = new WebClient())
                    {
                        client.Headers.Add("Authorization", "Bearer " + apiKey);
                        var json = await client.DownloadStringTaskAsync(DailiesUrl).ConfigureAwait(false);
                        ParseDailies(json);
                        _dailiesMessage = string.Empty;
                        _dailiesNeedsRetry = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to load Wizard's Vault dailies.");
                    _dailiesMessage = "Could not load dailies. Make sure the configured API key has the accountprogression permission.";
                    _dailiesNeedsRetry = true;
                }
            }
            finally
            {
                _dailiesLoading = false;
                _dailiesDirty = true;
            }
        }

        private static bool TryGetConfiguredApiKey(out string apiKey)
        {
            apiKey = string.Empty;

            try
            {
                var webApi = Gw2WebApi;
                var webApiType = webApi.GetType();

                // Prefer the key Blish HUD is currently using for this character.
                var connectionField = webApiType.GetField("_privilegedConnection", BindingFlags.Instance | BindingFlags.NonPublic);
                var connection = connectionField?.GetValue(webApi);
                if (connection != null)
                {
                    var hasKeyMethod = connection.GetType().GetMethod("HasApiKey");
                    if (hasKeyMethod != null && (bool)hasKeyMethod.Invoke(connection, null))
                    {
                        var connectionProperty = connection.GetType().GetProperty("Connection");
                        var innerConnection = connectionProperty?.GetValue(connection);
                        var tokenProperty = innerConnection?.GetType().GetProperty("AccessToken");
                        apiKey = tokenProperty?.GetValue(innerConnection) as string ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(apiKey)) return true;
                    }
                }

                // Fall back to any key registered in Blish HUD settings.
                var repositoryField = webApiType.GetField("_apiKeyRepository", BindingFlags.Instance | BindingFlags.NonPublic);
                if (repositoryField?.GetValue(webApi) is IEnumerable repository)
                {
                    foreach (var setting in repository)
                    {
                        var valueProperty = setting?.GetType().GetProperty("Value");
                        var value = valueProperty?.GetValue(setting) as string;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            apiKey = value;
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read a configured Blish HUD API key.");
                return false;
            }
        }

        private void ParseDailies(string json)
        {
            var root = JObject.Parse(json);

            var metaCurrent = root.Value<int?>("meta_progress_current") ?? 0;
            var metaComplete = root.Value<int?>("meta_progress_complete") ?? 0;
            var metaClaimed = root.Value<bool?>("meta_reward_claimed") ?? false;
            _dailies.Add($"Daily meta: {metaCurrent}/{metaComplete}{(metaClaimed ? " (claimed)" : string.Empty)}");

            if (!(root["objectives"] is JArray objectives)) return;

            foreach (var objective in objectives)
            {
                var title = objective.Value<string>("title") ?? "Objective";
                var current = objective.Value<int?>("progress_current") ?? 0;
                var complete = objective.Value<int?>("progress_complete") ?? 0;
                var claimed = objective.Value<bool?>("claimed") ?? false;
                var track = objective.Value<string>("track");
                var trackText = string.IsNullOrWhiteSpace(track) ? string.Empty : $" [{track}]";

                _dailies.Add($"{title}{trackText}  ({current}/{complete}){(claimed ? " ✓" : string.Empty)}");
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

            // Bottom left: Guild Wars 2 news.
            var bottomLeftPanel = new Panel()
            {
                Parent = parentPanel
            };

            var guildWarsHeader = new Panel()
            {
                Parent          = bottomLeftPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = HeadingHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = guildWarsHeader,
                Location       = new Point(10, 5),
                Text           = "GW2 News",
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var guildWarsContent = new Panel()
            {
                Parent           = bottomLeftPanel,
                Location         = new Point(0, HeadingHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            BuildFeedMenu(guildWarsContent, _guildWarsNews);

            // Top right: Wizard's Vault dailies.
            var topRightPanel = new Panel()
            {
                Parent = parentPanel
            };

            var dailiesHeader = new Panel()
            {
                Parent          = topRightPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = HeadingHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = dailiesHeader,
                Location       = new Point(10, 5),
                Text           = "Dailies",
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var dailiesContent = new Panel()
            {
                Parent           = topRightPanel,
                Location         = new Point(0, HeadingHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            dailiesContent.Resized += (sender, args) => UpdateDailiesMessageWidth(dailiesContent);

            _dailiesPanel = dailiesContent;
            BuildDailiesView(dailiesContent);
            _dailiesDirty = false;

            // Bottom right: tabbed Blish HUD module feeds.
            var bottomRightPanel = new Panel()
            {
                Parent = parentPanel
            };

            var moduleHeader = new Panel()
            {
                Parent          = bottomRightPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = HeadingHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = moduleHeader,
                Location       = new Point(10, 5),
                Text           = "Module News",
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var tabStrip = new FlowPanel()
            {
                Parent              = bottomRightPanel,
                Location            = new Point(0, HeadingHeight),
                WidthSizingMode     = SizingMode.Fill,
                Height              = TabStripHeight,
                FlowDirection       = ControlFlowDirection.SingleLeftToRight,
                ControlPadding      = new Vector2(4, 0),
                OuterControlPadding = new Vector2(8, 4)
            };

            var moduleUpdatesTab = new Label()
            {
                Parent          = tabStrip,
                Text            = "Module Updates",
                Size            = new Point(TabWidth, StandardButton.STANDARD_CONTROL_HEIGHT),
                TextColor       = Color.White,
                BackgroundColor = ActiveTabColor
            };

            var newModulesTab = new Label()
            {
                Parent          = tabStrip,
                Text            = "New Modules",
                Size            = new Point(TabWidth, StandardButton.STANDARD_CONTROL_HEIGHT),
                TextColor       = Color.LightGray,
                BackgroundColor = Color.Transparent
            };

            var contentPanel = new Panel()
            {
                Parent           = bottomRightPanel,
                Location         = new Point(0, HeadingHeight + TabStripHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            var newModulesPanel = new Panel()
            {
                Parent           = contentPanel,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill,
                Visible          = false
            };

            var moduleUpdatesPanel = new Panel()
            {
                Parent           = contentPanel,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            newModulesTab.Click += (sender, args) => SelectTab(newModulesTab, newModulesPanel, moduleUpdatesTab, moduleUpdatesPanel);
            moduleUpdatesTab.Click += (sender, args) => SelectTab(moduleUpdatesTab, moduleUpdatesPanel, newModulesTab, newModulesPanel);

            BuildFeedMenu(newModulesPanel, _newModules);
            BuildFeedMenu(moduleUpdatesPanel, _moduleUpdates);

            parentPanel.Resized += (sender, args) => UpdateQuadrants(parentPanel, topRightPanel, bottomLeftPanel, bottomRightPanel);
            UpdateQuadrants(parentPanel, topRightPanel, bottomLeftPanel, bottomRightPanel);

            return parentPanel;
        }

        private static void UpdateQuadrants(Panel parent, Panel topRight, Panel bottomLeft, Panel bottomRight)
        {
            var width  = parent.ContentRegion.Width;
            var height = parent.ContentRegion.Height;
            var quadrantWidth  = Math.Max(0, (width - QuadrantGap) / 2);
            var quadrantHeight = Math.Max(0, (height - QuadrantGap) / 2);
            var rightColumnX   = quadrantWidth + QuadrantGap;
            var bottomRowY     = quadrantHeight + QuadrantGap;
            var bottomHeight   = Math.Max(0, height - bottomRowY);

            topRight.Location = new Point(rightColumnX, 0);
            topRight.Size     = new Point(quadrantWidth, quadrantHeight);

            bottomLeft.Location = new Point(0, bottomRowY);
            bottomLeft.Size     = new Point(quadrantWidth, bottomHeight);

            bottomRight.Location = new Point(rightColumnX, bottomRowY);
            bottomRight.Size     = new Point(Math.Max(0, width - rightColumnX), bottomHeight);
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

        private void BuildDailiesView(Panel parent)
        {
            var scrollPanel = new Panel()
            {
                Parent           = parent,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill,
                CanScroll        = true
            };

            if (!string.IsNullOrWhiteSpace(_dailiesMessage))
            {
                new Label()
                {
                    Parent         = scrollPanel,
                    Location       = new Point(10, 10),
                    Width          = Math.Max(0, scrollPanel.ContentRegion.Width - 20),
                    Text           = _dailiesMessage,
                    Font           = Content.DefaultFont16,
                    TextColor      = Color.White,
                    WrapText       = true,
                    AutoSizeWidth  = false,
                    AutoSizeHeight = true
                };

                return;
            }

            var menu = new Menu()
            {
                Parent          = scrollPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                MenuItemHeight  = 36
            };

            foreach (var entry in _dailies)
            {
                menu.AddMenuItem(entry);
            }
        }

        private static void UpdateDailiesMessageWidth(Panel contentPanel)
        {
            foreach (var scrollPanel in contentPanel.Children.OfType<Panel>())
            {
                foreach (var label in scrollPanel.Children.OfType<Label>())
                {
                    label.Width = Math.Max(0, scrollPanel.ContentRegion.Width - 20);
                }
            }
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
            if (_dailiesNeedsRetry && !_dailiesLoading && DateTime.UtcNow >= _nextDailiesAttempt)
            {
                _nextDailiesAttempt = DateTime.UtcNow.AddSeconds(5);
                _ = LoadDailies();
            }

            if (_dailiesDirty && _dailiesPanel != null)
            {
                _dailiesPanel.ClearChildren();
                BuildDailiesView(_dailiesPanel);
                _dailiesDirty = false;
            }
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
