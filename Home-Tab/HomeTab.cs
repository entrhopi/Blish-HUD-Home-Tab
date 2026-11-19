using Blish_HUD;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
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
                Size = wndw.ContentRegion.Size
            };

            // Top left: newest Guild Wars 2 news entry.
            var topLeftPanel = new Panel()
            {
                Parent = parentPanel
            };

            var latestNewsHeader = new Panel()
            {
                Parent          = topLeftPanel,
                Location        = Point.Zero,
                WidthSizingMode = SizingMode.Fill,
                Height          = HeadingHeight,
                BackgroundColor = ActiveTabColor
            };

            new Label()
            {
                Parent         = latestNewsHeader,
                Location       = new Point(10, 5),
                Text           = "Latest News",
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var latestNewsContent = new Panel()
            {
                Parent           = topLeftPanel,
                Location         = new Point(0, HeadingHeight),
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            BuildFeaturedNews(latestNewsContent, _guildWarsNews.OrderByDescending(entry => entry.PublishedAt).FirstOrDefault());

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
                Text           = "More GW2 News",
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

            BuildFeedMenu(guildWarsContent, _guildWarsNews, 1);

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

            parentPanel.Resized += (sender, args) => UpdateQuadrants(parentPanel, topLeftPanel, topRightPanel, bottomLeftPanel, bottomRightPanel);
            UpdateQuadrants(parentPanel, topLeftPanel, topRightPanel, bottomLeftPanel, bottomRightPanel);

            return parentPanel;
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
            Gw2ApiManager.SubtokenUpdated -= OnSubtokenUpdated;
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
