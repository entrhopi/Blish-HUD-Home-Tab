using Blish_HUD;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Controls;
using Blish_HUD.Graphics.UI;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using entrhopi.HomeTab.Contracts;
using Gw2Sharp;
using Gw2Sharp.WebApi;
using Gw2Sharp.WebApi.Http;
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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TokenPermission = Gw2Sharp.WebApi.V2.Models.TokenPermission;
using static Blish_HUD.GameService;

namespace entrhopi.HomeTab
{
    internal enum HomeTabQuadrant
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    [Export(typeof(Blish_HUD.Modules.Module))]
    public class Module : Blish_HUD.Modules.Module
    {

        private const string NewModulesFeedUrl = "https://pkgs.blishhud.com/new-modules.xml";
        private const string ModuleUpdatesFeedUrl = "https://pkgs.blishhud.com/module-updates.xml";
        private const string GuildWarsFeedUrl = "https://www.guildwars2.com/en/feed/";
        private const string DailiesUrl = "https://api.guildwars2.com/v2/account/wizardsvault/daily";
        private const string NoneBlockId = "";
        private const string LatestNewsBlockId = "entrhopi.home.latest-news";
        private const string Gw2NewsBlockId = "entrhopi.home.gw2-news";
        private const string DailiesBlockId = "entrhopi.home.dailies";
        private const string ModuleUpdatesBlockId = "entrhopi.home.module-updates";
        private const string NewModulesBlockId = "entrhopi.home.new-modules";
        private const int HeadingHeight = 36;
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
        private readonly Dictionary<string, Task> _feedLoads = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private string _dailiesMessage = string.Empty;
        private Panel _dailiesPanel;
        private Panel _homeTabPanel;
        private Panel _topLeftPanel;
        private Panel _topRightPanel;
        private Panel _bottomLeftPanel;
        private Panel _bottomRightPanel;
        private bool _dailiesDirty;
        private bool _dailiesNeedsRetry;
        private bool _dailiesLoading;
        private bool _dailiesLoaded;
        private DateTime _nextDailiesAttempt = DateTime.MinValue;
        private SettingEntry<string> _topLeftBlock;
        private SettingEntry<string> _topRightBlock;
        private SettingEntry<string> _bottomLeftBlock;
        private SettingEntry<string> _bottomRightBlock;
        private bool _normalizingAssignments;
        private int _refreshVersion;

        internal event EventHandler AssignmentsChanged;
        internal event EventHandler BlocksChanged;

        internal string TabName = "Home";

        [ImportingConstructor]
        public Module([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) { }

        protected override void DefineSettings(SettingCollection settings)
        {
            _topLeftBlock = settings.DefineSetting(
                "TopLeftBlock",
                LatestNewsBlockId,
                () => "Top left",
                () => "Block shown in the top-left quadrant.");

            _topRightBlock = settings.DefineSetting(
                "TopRightBlock",
                DailiesBlockId,
                () => "Top right",
                () => "Block shown in the top-right quadrant.");

            _bottomLeftBlock = settings.DefineSetting(
                "BottomLeftBlock",
                Gw2NewsBlockId,
                () => "Bottom left",
                () => "Block shown in the bottom-left quadrant.");

            _bottomRightBlock = settings.DefineSetting(
                "BottomRightBlock",
                ModuleUpdatesBlockId,
                () => "Bottom right",
                () => "Block shown in the bottom-right quadrant.");

            _topLeftBlock.SettingChanged += OnAssignmentChanged;
            _topRightBlock.SettingChanged += OnAssignmentChanged;
            _bottomLeftBlock.SettingChanged += OnAssignmentChanged;
            _bottomRightBlock.SettingChanged += OnAssignmentChanged;
        }

        public override IView GetSettingsView()
        {
            return new HomeTabSettingsView(this);
        }

        protected override void Initialize()
        {
            _homeIcon = ContentsManager.GetTexture("255369.png");
        }

        protected override async Task LoadAsync()
        {
            await EnsureAssignedBlockDataAsync().ConfigureAwait(false);
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
                if (!Gw2ApiManager.HasSubtoken)
                {
                    _dailiesMessage = string.IsNullOrWhiteSpace(Gw2Mumble.PlayerCharacter.Name)
                        ? "Log in to a character so Blish HUD can obtain an API token. Dailies will load automatically."
                        : "Blish HUD has not provided an API token yet. Check that an API key is configured in Blish HUD.";
                    _dailiesNeedsRetry = true;
                    return;
                }

                try
                {
                    var json = await GetWizardsVaultDailyJson().ConfigureAwait(false);
                    ParseDailies(json);
                    _dailiesMessage = string.Empty;
                    _dailiesNeedsRetry = false;
                    _dailiesLoaded = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to load Wizard's Vault dailies.");

                    if (!Gw2ApiManager.HasPermissions(new[] { TokenPermission.Account, TokenPermission.Progression }))
                    {
                        _dailiesMessage = "Blish HUD has not passed the account and progression permissions to this module yet. Disable and re-enable Home Tab once after checking them.";
                    }
                    else
                    {
                        _dailiesMessage = "Could not load dailies. Make sure your API key has the account and progression permissions.";
                    }

                    _dailiesNeedsRetry = true;
                }
            }
            finally
            {
                _dailiesLoading = false;
                _dailiesDirty = true;
            }
        }

        private SettingEntry<string>[] GetAssignmentSettings()
        {
            return new[] { _topLeftBlock, _topRightBlock, _bottomLeftBlock, _bottomRightBlock };
        }

        internal string GetAssignedBlockId(HomeTabQuadrant quadrant)
        {
            switch (quadrant)
            {
                case HomeTabQuadrant.TopLeft: return _topLeftBlock?.Value ?? NoneBlockId;
                case HomeTabQuadrant.TopRight: return _topRightBlock?.Value ?? NoneBlockId;
                case HomeTabQuadrant.BottomLeft: return _bottomLeftBlock?.Value ?? NoneBlockId;
                case HomeTabQuadrant.BottomRight: return _bottomRightBlock?.Value ?? NoneBlockId;
                default: return NoneBlockId;
            }
        }

        internal void SetAssignedBlockId(HomeTabQuadrant quadrant, string blockId)
        {
            var setting = GetAssignmentSetting(quadrant);
            if (setting == null) return;

            setting.Value = blockId ?? NoneBlockId;
        }

        private SettingEntry<string> GetAssignmentSetting(HomeTabQuadrant quadrant)
        {
            switch (quadrant)
            {
                case HomeTabQuadrant.TopLeft: return _topLeftBlock;
                case HomeTabQuadrant.TopRight: return _topRightBlock;
                case HomeTabQuadrant.BottomLeft: return _bottomLeftBlock;
                case HomeTabQuadrant.BottomRight: return _bottomRightBlock;
                default: return null;
            }
        }

        private void OnAssignmentChanged(object sender, ValueChangedEventArgs<string> e)
        {
            if (_normalizingAssignments) return;

            NormalizeAssignments(sender as SettingEntry<string>, e.NewValue);
            AssignmentsChanged?.Invoke(this, EventArgs.Empty);
            _ = RefreshAssignmentsAsync();
        }

        private void NormalizeAssignments(SettingEntry<string> preferredSetting = null, string preferredValue = null)
        {
            _normalizingAssignments = true;

            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (preferredSetting != null && !string.IsNullOrWhiteSpace(preferredValue))
                {
                    seen.Add(preferredValue);
                }

                foreach (var setting in GetAssignmentSettings())
                {
                    if (setting == null
                     || setting == preferredSetting
                     || string.IsNullOrWhiteSpace(setting.Value))
                    {
                        continue;
                    }

                    if (!seen.Add(setting.Value))
                    {
                        setting.Value = NoneBlockId;
                    }
                }
            }
            finally
            {
                _normalizingAssignments = false;
            }
        }

        private async Task RefreshAssignmentsAsync()
        {
            int version = ++_refreshVersion;

            await EnsureAssignedBlockDataAsync().ConfigureAwait(false);

            if (version != _refreshVersion) return;

            Overlay.QueueMainThreadUpdate(_ =>
            {
                if (version == _refreshVersion)
                {
                    RebuildHomeTab();
                }
            });
        }

        private async Task EnsureAssignedBlockDataAsync()
        {
            var assigned = new HashSet<string>(
                GetAssignmentSettings().Where(setting => setting != null).Select(setting => setting.Value),
                StringComparer.OrdinalIgnoreCase);

            var tasks = new List<Task>();

            if (assigned.Contains(LatestNewsBlockId) || assigned.Contains(Gw2NewsBlockId))
            {
                tasks.Add(EnsureFeedLoadedAsync(GuildWarsFeedUrl, _guildWarsNews));
            }

            if (assigned.Contains(ModuleUpdatesBlockId))
            {
                tasks.Add(EnsureFeedLoadedAsync(ModuleUpdatesFeedUrl, _moduleUpdates));
            }

            if (assigned.Contains(NewModulesBlockId))
            {
                tasks.Add(EnsureFeedLoadedAsync(NewModulesFeedUrl, _newModules));
            }

            if (assigned.Contains(DailiesBlockId))
            {
                tasks.Add(EnsureDailiesLoadedAsync());
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private Task EnsureFeedLoadedAsync(string url, List<RssItem> target)
        {
            lock (_feedLoads)
            {
                if (_feedLoads.TryGetValue(url, out var existingTask))
                {
                    return existingTask;
                }

                var loadTask = LoadFeed(url, target);
                _feedLoads[url] = loadTask;
                return loadTask;
            }
        }

        private Task EnsureDailiesLoadedAsync()
        {
            if (_dailiesLoaded || _dailiesLoading) return Task.CompletedTask;

            return LoadDailies();
        }

        internal bool IsBlockAssigned(string blockId)
        {
            return GetAssignmentSettings()
                  .Any(setting => setting != null && string.Equals(setting.Value, blockId, StringComparison.OrdinalIgnoreCase));
        }

        internal IReadOnlyList<IHomeTabBlock> GetAvailableBlocks()
        {
            var blocks = new List<IHomeTabBlock>()
            {
                new HomeTabBlock(LatestNewsBlockId, "Latest News", CreateLatestNewsContent),
                new HomeTabBlock(Gw2NewsBlockId, "GW2 News", CreateGw2NewsContent),
                new HomeTabBlock(DailiesBlockId, "Dailies", CreateDailiesContent),
                new HomeTabBlock(ModuleUpdatesBlockId, "Module Updates", CreateModuleUpdatesContent),
                new HomeTabBlock(NewModulesBlockId, "New Modules", CreateNewModulesContent)
            };

            try
            {
                foreach (var externalBlock in HomeTabBlockRegistry.GetBlocks())
                {
                    if (blocks.All(block => !string.Equals(block.Id, externalBlock.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        blocks.Add(externalBlock);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read external Home Tab blocks.");
            }

            return blocks.OrderBy(block => block.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private void OnRegistryBlocksChanged(object sender, EventArgs e)
        {
            Overlay.QueueMainThreadUpdate(_ =>
            {
                BlocksChanged?.Invoke(this, EventArgs.Empty);
                RebuildHomeTab();
            });
        }

        private Control CreateLatestNewsContent()
        {
            var root = new Panel();
            BuildFeaturedNews(root, _guildWarsNews.OrderByDescending(entry => entry.PublishedAt).FirstOrDefault());
            return root;
        }

        private Control CreateGw2NewsContent()
        {
            var root = new Panel();
            BuildFeedMenu(root, _guildWarsNews);
            return root;
        }

        private Control CreateDailiesContent()
        {
            var root = new Panel();
            _dailiesPanel = root;
            BuildDailiesView(root);
            _dailiesDirty = false;
            return root;
        }

        private Control CreateModuleUpdatesContent()
        {
            var root = new Panel();
            BuildFeedMenu(root, _moduleUpdates);
            return root;
        }

        private Control CreateNewModulesContent()
        {
            var root = new Panel();
            BuildFeedMenu(root, _newModules);
            return root;
        }

        private static Control CreateMessageContent(string message)
        {
            var root = new Panel();
            var scrollPanel = new Panel()
            {
                Parent           = root,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            scrollPanel.CanScroll = true;

            var label = new Label()
            {
                Parent         = scrollPanel,
                Text           = message,
                Font           = Content.DefaultFont16,
                TextColor      = Color.White,
                WrapText       = true,
                AutoSizeWidth  = false,
                AutoSizeHeight = true
            };

            scrollPanel.Resized += (sender, args) => LayoutDailiesMessage(scrollPanel, label, message);
            root.Resized += (sender, args) => LayoutDailiesMessage(scrollPanel, label, message);
            LayoutDailiesMessage(scrollPanel, label, message);

            return root;
        }

        private sealed class HomeTabBlock : IHomeTabBlock
        {
            private readonly Func<Control> _createContent;

            public string Id { get; }
            public string DisplayName { get; }

            public HomeTabBlock(string id, string displayName, Func<Control> createContent)
            {
                Id = id;
                DisplayName = displayName;
                _createContent = createContent;
            }

            public Control CreateContent()
            {
                return _createContent();
            }
        }

        private async Task<string> GetWizardsVaultDailyJson()
        {
            // Use the Gw2Sharp client supplied by Blish HUD. The module never
            // reads the user's API key; Blish HUD attaches its managed subtoken.
            var apiClient = Gw2ApiManager.Gw2ApiClient;
            var baseType = typeof(Gw2WebApiBaseClient);
            var connection = baseType.GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(apiClient) as IConnection;
            var gw2Client = baseType.GetProperty("Gw2Client", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(apiClient) as IGw2Client;

            if (connection == null || gw2Client == null)
            {
                throw new InvalidOperationException("Could not access Blish HUD's managed GW2 API connection.");
            }

            var request = new WebApiRequest(new Uri(DailiesUrl), connection, gw2Client);
            var response = await request.ExecuteAsync<JsonElement>().ConfigureAwait(false);

            return response.Content.GetRawText();
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

            SelectModuleTab();

            Gw2ApiManager.SubtokenUpdated += OnSubtokenUpdated;
            HomeTabBlockRegistry.BlocksChanged += OnRegistryBlocksChanged;

            // Base handler must be called
            base.OnModuleLoaded(e);
        }

        private void OnSubtokenUpdated(object sender, ValueEventArgs<IEnumerable<TokenPermission>> e)
        {
            _dailiesNeedsRetry = true;
            _nextDailiesAttempt = DateTime.MinValue;
        }

        private void SelectModuleTab()
        {
            if (_moduleTab == null) return;

            // Blish HUD does not expose the tab collection, only SelectedTabIndex.
            // Read the private list so we can select our tab without assuming it is index 0.
            var tabsField = typeof(TabbedWindow).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);
            if (tabsField?.GetValue(Overlay.BlishHudWindow) is IList tabs)
            {
                var index = tabs.IndexOf(_moduleTab);
                if (index >= 0)
                {
                    Overlay.BlishHudWindow.SelectedTabIndex = index;
                    return;
                }
            }

            // Fallback for Blish HUD versions where the private field changes.
            Overlay.BlishHudWindow.SelectedTabIndex = 0;
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
                Size      = wndw.ContentRegion.Size
            };

            _homeTabPanel = parentPanel;
            parentPanel.Resized += HomeTabOnResized;

            BuildQuadrants(parentPanel);

            return parentPanel;
        }

        private void HomeTabOnResized(object sender, ResizedEventArgs e)
        {
            UpdateQuadrants(_homeTabPanel, _topLeftPanel, _topRightPanel, _bottomLeftPanel, _bottomRightPanel);
        }

        private void RebuildHomeTab()
        {
            if (_homeTabPanel == null) return;

            _homeTabPanel.ClearChildren();
            BuildQuadrants(_homeTabPanel);
        }

        private void BuildQuadrants(Panel parent)
        {
            _dailiesPanel = null;

            _topLeftPanel = new Panel() { Parent = parent };
            _topRightPanel = new Panel() { Parent = parent };
            _bottomLeftPanel = new Panel() { Parent = parent };
            _bottomRightPanel = new Panel() { Parent = parent };

            UpdateQuadrants(parent, _topLeftPanel, _topRightPanel, _bottomLeftPanel, _bottomRightPanel);

            var blocks = GetAvailableBlocks()
                         .GroupBy(block => block.Id, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            BuildQuadrant(_topLeftPanel, GetAssignedBlockId(HomeTabQuadrant.TopLeft), blocks);
            BuildQuadrant(_topRightPanel, GetAssignedBlockId(HomeTabQuadrant.TopRight), blocks);
            BuildQuadrant(_bottomLeftPanel, GetAssignedBlockId(HomeTabQuadrant.BottomLeft), blocks);
            BuildQuadrant(_bottomRightPanel, GetAssignedBlockId(HomeTabQuadrant.BottomRight), blocks);
        }

        private void BuildQuadrant(Panel quadrant, string blockId, IDictionary<string, IHomeTabBlock> blocks)
        {
            if (string.IsNullOrWhiteSpace(blockId)) return;

            IHomeTabBlock block = null;
            blocks.TryGetValue(blockId, out block);

            string headerText = block?.DisplayName ?? "Unavailable Block";
            var header = CreateBlockHeader(quadrant, headerText);

            var contentPanel = new Panel()
            {
                Parent           = quadrant,
                Location         = new Point(0, HeadingHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            Control content;

            if (block == null)
            {
                content = CreateMessageContent("This block is unavailable. Enable the module that provides it or choose another block in the Home Tab settings.");
            }
            else
            {
                try
                {
                    content = block.CreateContent();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Home Tab block {blockId} failed to create its content.", blockId);
                    content = CreateMessageContent("This block failed to load.");
                }
            }

            if (content == null)
            {
                content = CreateMessageContent("This block does not provide any content.");
            }

            content.Parent   = contentPanel;
            content.Location = Point.Zero;
            content.Size     = new Point(contentPanel.Width, contentPanel.Height);

            if (content is Container container)
            {
                container.WidthSizingMode  = SizingMode.Fill;
                container.HeightSizingMode = SizingMode.Fill;
            }

            contentPanel.Resized += (sender, args) =>
            {
                if (content.Parent != null)
                {
                    content.Size = new Point(contentPanel.Width, contentPanel.Height);
                }
            };
        }

        private static Panel CreateBlockHeader(Panel quadrant, string title)
        {
            var header = new Panel()
            {
                Parent          = quadrant,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = HeadingHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = header,
                Location       = new Point(10, 5),
                Text           = title,
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            return header;
        }

        private static void UpdateQuadrants(Panel parent, Panel topLeft, Panel topRight, Panel bottomLeft, Panel bottomRight)
        {
            var width  = parent.Width;
            var height = parent.Height;
            var quadrantWidth  = Math.Max(0, (width - QuadrantGap) / 2);
            var quadrantHeight = Math.Max(0, (height - QuadrantGap) / 2);
            var rightColumnX   = quadrantWidth + QuadrantGap;
            var bottomRowY     = quadrantHeight + QuadrantGap;
            var bottomHeight   = Math.Max(0, height - bottomRowY);

            topLeft.Location = Point.Zero;
            topLeft.Size     = new Point(quadrantWidth, quadrantHeight);

            topRight.Location = new Point(rightColumnX, 0);
            topRight.Size     = new Point(quadrantWidth, quadrantHeight);

            bottomLeft.Location = new Point(0, bottomRowY);
            bottomLeft.Size     = new Point(quadrantWidth, bottomHeight);

            bottomRight.Location = new Point(rightColumnX, bottomRowY);
            bottomRight.Size     = new Point(Math.Max(0, width - rightColumnX), bottomHeight);
        }

        private void BuildDailiesView(Panel parent)
        {
            var scrollPanel = new Panel()
            {
                Parent           = parent,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            // Panel.CanScroll must be set after the panel has its size/parent set.
            scrollPanel.CanScroll = true;

            if (!string.IsNullOrWhiteSpace(_dailiesMessage))
            {
                var message = new Label()
                {
                    Parent         = scrollPanel,
                    Text           = _dailiesMessage,
                    Font           = Content.DefaultFont16,
                    TextColor      = Color.White,
                    WrapText       = true,
                    AutoSizeWidth  = false,
                    AutoSizeHeight = true
                };

                scrollPanel.Resized += (sender, args) => LayoutDailiesMessage(parent, message, _dailiesMessage);
                parent.Resized += (sender, args) => LayoutDailiesMessage(parent, message, _dailiesMessage);
                scrollPanel.ContentResized += (sender, args) => LayoutDailiesMessage(parent, message, _dailiesMessage);
                LayoutDailiesMessage(parent, message, _dailiesMessage);
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

        private static void LayoutDailiesMessage(Panel parent, Label message, string text)
        {
            var width = GetPaddedContentWidth(parent);
            var wrapped = WrapTextForWidth(message, text, width);

            message.WrapText = false;
            if (message.Text != wrapped)
            {
                message.Text = wrapped;
            }

            if (message.Width != width)
            {
                message.Width = width;
            }

            RefreshWrappedLabel(message);
            message.Location = new Point(10, 10);
        }

        private static void RefreshWrappedLabel(Label label)
        {
            // LabelBase wraps text using the previous LabelRegion width, so a
            // second pass is needed after the width changes to reflow correctly.
            label.RecalculateLayout();
            label.RecalculateLayout();
        }

        private static int GetPaddedContentWidth(Panel parent)
        {
            var width = parent.Width;

            if (parent.Parent != null)
            {
                width = Math.Min(width, parent.Parent.Width);
            }

            return Math.Max(0, width - 20);
        }

        private static string WrapTextForWidth(Label label, string text, int maxWidth)
        {
            if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0) return text;

            var font = label.Font;
            var result = new StringBuilder();

            foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = new StringBuilder();

                foreach (var word in paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;

                    if (font.MeasureString(candidate).Width <= maxWidth)
                    {
                        if (line.Length > 0)
                        {
                            line.Append(' ');
                        }

                        line.Append(word);
                        continue;
                    }

                    if (line.Length > 0)
                    {
                        result.Append(line).Append('\n');
                        line.Clear();
                    }

                    var remaining = word;
                    while (remaining.Length > 1 && font.MeasureString(remaining).Width > maxWidth)
                    {
                        var length = remaining.Length - 1;
                        while (length > 1 && font.MeasureString(remaining.Substring(0, length)).Width > maxWidth)
                        {
                            length--;
                        }

                        result.Append(remaining.Substring(0, length)).Append('\n');
                        remaining = remaining.Substring(length);
                    }

                    line.Append(remaining);
                }

                result.Append(line).Append('\n');
            }

            return result.ToString().TrimEnd('\n');
        }

        private static void BuildFeaturedNews(Panel parent, RssItem item)
        {
            var scrollPanel = new Panel()
            {
                Parent           = parent,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            // Panel.CanScroll must be set after the panel has its size/parent set.
            scrollPanel.CanScroll = true;

            var labels = new List<KeyValuePair<Label, string>>();

            if (item == null)
            {
                var noNews = new Label()
                {
                    Parent         = scrollPanel,
                    Text           = "No news available.",
                    Font           = Content.DefaultFont14,
                    TextColor      = Color.White,
                    WrapText       = true,
                    AutoSizeWidth  = false,
                    AutoSizeHeight = true
                };

                labels.Add(new KeyValuePair<Label, string>(noNews, "No news available."));
            }
            else
            {
                var title = new Label()
                {
                    Parent         = scrollPanel,
                    Text           = item.Title,
                    Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                    TextColor      = Color.White,
                    WrapText       = true,
                    AutoSizeWidth  = false,
                    AutoSizeHeight = true
                };

                if (!string.IsNullOrWhiteSpace(item.Link))
                {
                    title.BasicTooltipText = "Open in browser";
                    title.Click += (sender, args) => OpenLink(item.Link);
                }

                labels.Add(new KeyValuePair<Label, string>(title, item.Title));

                var meta = string.Join("  •  ", new[] { item.Published, item.Author }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(meta))
                {
                    var metaLabel = new Label()
                    {
                        Parent         = scrollPanel,
                        Text           = meta,
                        Font           = Content.DefaultFont14,
                        TextColor      = Color.LightGray,
                        WrapText       = true,
                        AutoSizeWidth  = false,
                        AutoSizeHeight = true
                    };

                    labels.Add(new KeyValuePair<Label, string>(metaLabel, meta));
                }

                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    var description = new Label()
                    {
                        Parent         = scrollPanel,
                        Text           = item.Description,
                        Font           = Content.DefaultFont16,
                        TextColor      = Color.White,
                        WrapText       = true,
                        AutoSizeWidth  = false,
                        AutoSizeHeight = true
                    };

                    labels.Add(new KeyValuePair<Label, string>(description, item.Description));
                }

                if (!string.IsNullOrWhiteSpace(item.Link))
                {
                    var link = new Label()
                    {
                        Parent           = scrollPanel,
                        Text             = "Open in browser",
                        Font             = Content.DefaultFont12,
                        TextColor        = Color.LightGray,
                        WrapText         = true,
                        AutoSizeWidth    = false,
                        AutoSizeHeight   = true,
                        BasicTooltipText = item.Link
                    };

                    link.Click += (sender, args) => OpenLink(item.Link);
                    labels.Add(new KeyValuePair<Label, string>(link, "Open in browser"));
                }
            }

            scrollPanel.Resized += (sender, args) => LayoutFeaturedNews(parent, labels);
            parent.Resized += (sender, args) => LayoutFeaturedNews(parent, labels);
            scrollPanel.ContentResized += (sender, args) => LayoutFeaturedNews(parent, labels);
            LayoutFeaturedNews(parent, labels);
        }

        private static void LayoutFeaturedNews(Panel parent, IList<KeyValuePair<Label, string>> labels)
        {
            var width = GetPaddedContentWidth(parent);
            var y = 10;

            foreach (var entry in labels)
            {
                var label = entry.Key;
                var wrapped = WrapTextForWidth(label, entry.Value, width);

                label.WrapText = false;
                if (label.Text != wrapped)
                {
                    label.Text = wrapped;
                }

                if (label.Width != width)
                {
                    label.Width = width;
                }

                RefreshWrappedLabel(label);
                label.Location = new Point(10, y);
                y += label.Height + 6;
            }
        }

        private static void BuildFeedMenu(Panel parent, IEnumerable<RssItem> items, int skip = 0)
        {
            var scrollPanel = new Panel()
            {
                Parent           = parent,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            // Panel.CanScroll must be set after the panel has its size/parent set.
            scrollPanel.CanScroll = true;

            var menu = new Menu()
            {
                Parent          = scrollPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                MenuItemHeight  = 36
            };

            foreach (var item in items
                .OrderByDescending(entry => entry.PublishedAt)
                .Skip(skip)
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
            if (_dailiesNeedsRetry
             && IsBlockAssigned(DailiesBlockId)
             && !_dailiesLoading
             && DateTime.UtcNow >= _nextDailiesAttempt)
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
            Gw2ApiManager.SubtokenUpdated -= OnSubtokenUpdated;
            HomeTabBlockRegistry.BlocksChanged -= OnRegistryBlocksChanged;

            _topLeftBlock.SettingChanged -= OnAssignmentChanged;
            _topRightBlock.SettingChanged -= OnAssignmentChanged;
            _bottomLeftBlock.SettingChanged -= OnAssignmentChanged;
            _bottomRightBlock.SettingChanged -= OnAssignmentChanged;

            _homeTabPanel = null;
            _dailiesPanel = null;
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
