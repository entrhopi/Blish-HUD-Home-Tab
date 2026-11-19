using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.Graphics.UI;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Blish_HUD.GameService;

namespace entrhopi.HomeTab
{
    internal sealed class HomeTabSettingsView : View
    {
        private const int DropdownWidth = 320;
        private const int RowHeight = 44;
        private const int LabelWidth = 220;

        private readonly Module _module;
        private readonly Dictionary<HomeTabQuadrant, Dropdown> _dropdowns =
            new Dictionary<HomeTabQuadrant, Dropdown>();
        private readonly Dictionary<HomeTabQuadrant, List<string>> _choiceIds =
            new Dictionary<HomeTabQuadrant, List<string>>();

        private bool _updating;

        public HomeTabSettingsView(Module module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
        }

        protected override Task<bool> Load(IProgress<string> progress)
        {
            _module.AssignmentsChanged += OnAssignmentsChanged;
            _module.BlocksChanged += OnBlocksChanged;
            return base.Load(progress);
        }

        protected override void Build(Container buildPanel)
        {
            var root = new Panel()
            {
                Parent           = buildPanel,
                Location         = Point.Zero,
                WidthSizingMode  = SizingMode.Fill,
                HeightSizingMode = SizingMode.Fill
            };

            root.CanScroll = true;

            new Label()
            {
                Parent         = root,
                Location       = new Point(10, 10),
                Text           = "Home Tab layout",
                Font           = Content.GetFont(ContentService.FontFace.Menomonia, ContentService.FontSize.Size24, ContentService.FontStyle.Regular),
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            new Label()
            {
                Parent         = root,
                Location       = new Point(10, 48),
                Text           = "Choose which block appears in each quadrant. Selecting a block that is already in use moves it to the new quadrant.",
                Font           = Content.DefaultFont14,
                TextColor      = Color.LightGray,
                WrapText       = true,
                AutoSizeWidth  = false,
                AutoSizeHeight = true,
                Width          = Math.Max(0, buildPanel.ContentRegion.Width - 20)
            };

            AddRow(root, HomeTabQuadrant.TopLeft, "Top left", 86);
            AddRow(root, HomeTabQuadrant.TopRight, "Top right", 86 + RowHeight);
            AddRow(root, HomeTabQuadrant.BottomLeft, "Bottom left", 86 + RowHeight * 2);
            AddRow(root, HomeTabQuadrant.BottomRight, "Bottom right", 86 + RowHeight * 3);

            RefreshDropdowns();
        }

        private void AddRow(Panel root, HomeTabQuadrant quadrant, string labelText, int top)
        {
            var row = new Panel()
            {
                Parent          = root,
                Location        = new Point(10, top),
                WidthSizingMode = SizingMode.Fill,
                Height          = RowHeight
            };

            new Label()
            {
                Parent         = row,
                Location       = new Point(0, 10),
                Text           = labelText,
                Font           = Content.DefaultFont16,
                TextColor      = Color.White,
                AutoSizeWidth  = true,
                AutoSizeHeight = true
            };

            var dropdown = new Dropdown()
            {
                Parent   = row,
                Location = new Point(LabelWidth, 4),
                Width    = DropdownWidth,
                Height   = 32
            };

            dropdown.ValueChanged += (sender, args) => OnDropdownChanged(quadrant);
            _dropdowns[quadrant] = dropdown;
        }

        private void OnDropdownChanged(HomeTabQuadrant quadrant)
        {
            if (_updating) return;

            var dropdown = _dropdowns[quadrant];
            var index = dropdown.Items.IndexOf(dropdown.SelectedItem);
            if (index < 0 || index >= _choiceIds[quadrant].Count) return;

            _module.SetAssignedBlockId(quadrant, _choiceIds[quadrant][index]);
        }

        private void OnAssignmentsChanged(object sender, EventArgs e)
        {
            RefreshDropdowns();
        }

        private void OnBlocksChanged(object sender, EventArgs e)
        {
            RefreshDropdowns();
        }

        private void RefreshDropdowns()
        {
            if (_dropdowns.Count == 0) return;

            _updating = true;

            try
            {
                var blocks = _module.GetAvailableBlocks();

                foreach (var pair in _dropdowns)
                {
                    var quadrant = pair.Key;
                    var dropdown = pair.Value;
                    var currentId = _module.GetAssignedBlockId(quadrant);

                    var choices = BuildChoices(blocks);
                    if (!string.IsNullOrWhiteSpace(currentId)
                     && choices.All(choice => !string.Equals(choice.Id, currentId, StringComparison.OrdinalIgnoreCase)))
                    {
                        choices.Add((currentId, $"{currentId} (unavailable)"));
                    }

                    _choiceIds[quadrant] = choices.Select(choice => choice.Id).ToList();

                    dropdown.Items.Clear();

                    foreach (var choice in choices)
                    {
                        dropdown.Items.Add(choice.Label);
                    }

                    var selectedIndex = choices.FindIndex(choice =>
                        string.Equals(choice.Id, currentId, StringComparison.OrdinalIgnoreCase));

                    dropdown.SelectedItem = selectedIndex >= 0
                        ? dropdown.Items[selectedIndex]
                        : dropdown.Items.FirstOrDefault();
                }
            }
            finally
            {
                _updating = false;
            }
        }

        private static List<(string Id, string Label)> BuildChoices(IEnumerable<Contracts.IHomeTabBlock> blocks)
        {
            var choices = new List<(string Id, string Label)>
            {
                (string.Empty, "None")
            };

            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var block in blocks)
            {
                var label = block.DisplayName;

                if (!names.Add(label))
                {
                    label = $"{label} [{block.Id}]";
                }

                choices.Add((block.Id, label));
            }

            return choices;
        }

        protected override void Unload()
        {
            _module.AssignmentsChanged -= OnAssignmentsChanged;
            _module.BlocksChanged -= OnBlocksChanged;
        }
    }
}
