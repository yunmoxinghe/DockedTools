using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using DockedTools.Features.Pages.WebApp.Browser.Services;
using DockedTools.Features.UnifiedCalls.TopAppBar;

namespace DockedTools.Features.Pages.Settings
{
    /// <summary>
    /// 自适应栏色设置页。
    /// 选项与默认值对齐 ATBC 的 defaultPreferenceContent（见 <see cref="AdaptiveColourSettings"/>）。
    /// </summary>
    public sealed partial class AdaptiveColourPage : Page
    {
        private readonly 智能标题 _智能标题 = new();

        /// <summary>站点规则表（ATBC: ruleList）</summary>
        private readonly ObservableCollection<AdaptiveColourRule> _rules = new();

        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        public AdaptiveColourPage()
        {
            InitializeComponent();
            RuleItemsControl.ItemsSource = _rules;
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, PageScrollViewer, PageTitleBlock);
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            UpdateMargin();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (System.Math.Abs(e.NewSize.Width - _lastMeasuredWidth) < 1) return;
            UpdateMargin();
        }

        private void UpdateMargin()
        {
            double width = RootGrid?.ActualWidth ?? ActualWidth;
            if (width <= 0) return;
            double normalized = System.Math.Clamp((width - MinResponsiveWidth) / (MaxResponsiveWidth - MinResponsiveWidth), 0, 1);
            double margin = System.Math.Round(MinHorizontalMargin + (MaxHorizontalMargin - MinHorizontalMargin) * normalized);
            if (System.Math.Abs(margin - _lastAppliedMargin) > 0.01)
            {
                PageContentPanel.Margin = new Thickness(margin, 0, margin, 0);
                _lastAppliedMargin = margin;
            }
            _lastMeasuredWidth = width;
        }

        private void LoadSettings()
        {
            EnabledToggle.Toggled -= OnEnabledToggled;
            EnabledToggle.IsOn = AdaptiveColourSettings.Enabled;
            EnabledToggle.Toggled += OnEnabledToggled;

            DynamicToggle.Toggled -= OnDynamicToggled;
            DynamicToggle.IsOn = AdaptiveColourSettings.Dynamic;
            DynamicToggle.Toggled += OnDynamicToggled;

            SchemeComboBox.SelectionChanged -= OnSchemeChanged;
            SchemeComboBox.SelectedIndex = (int)AdaptiveColourSettings.Scheme;
            SchemeComboBox.SelectionChanged += OnSchemeChanged;

            NoThemeColourToggle.Toggled -= OnNoThemeColourToggled;
            NoThemeColourToggle.IsOn = AdaptiveColourSettings.NoThemeColour;
            NoThemeColourToggle.Toggled += OnNoThemeColourToggled;

            AllowDarkLightToggle.Toggled -= OnAllowDarkLightToggled;
            AllowDarkLightToggle.IsOn = AdaptiveColourSettings.AllowDarkLight;
            AllowDarkLightToggle.Toggled += OnAllowDarkLightToggled;

            // NumberBox 赋值会触发 ValueChanged，加载期间必须先脱钩，否则会把默认值又写回去
            TabBarBox.ValueChanged -= OnTabBarChanged;
            TabBarBox.Value = AdaptiveColourSettings.TabBar;
            TabBarBox.ValueChanged += OnTabBarChanged;

            MinContrastLightBox.ValueChanged -= OnMinContrastLightChanged;
            MinContrastLightBox.Value = AdaptiveColourSettings.MinContrastLight;
            MinContrastLightBox.ValueChanged += OnMinContrastLightChanged;

            MinContrastDarkBox.ValueChanged -= OnMinContrastDarkChanged;
            MinContrastDarkBox.Value = AdaptiveColourSettings.MinContrastDark;
            MinContrastDarkBox.ValueChanged += OnMinContrastDarkChanged;

            FallbackLightTextBox.Text = AdaptiveColourSettings.FallbackLight;
            FallbackDarkTextBox.Text = AdaptiveColourSettings.FallbackDark;
            QueryTextBox.Text = AdaptiveColourSettings.Query;

            LoadRules();
        }

        private void LoadRules()
        {
            _rules.Clear();

            foreach (AdaptiveColourRule rule in AdaptiveColourSettings.Rules)
            {
                _rules.Add(rule.Clone());
            }

            RuleEmptyHint.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAddRuleClick(object sender, RoutedEventArgs e)
        {
            string host = RuleHostTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(host))
            {
                return;
            }

            AdaptiveRuleType type = RuleTypeComboBox.SelectedIndex switch
            {
                0 => AdaptiveRuleType.Colour,
                2 => AdaptiveRuleType.QuerySelector,
                _ => AdaptiveRuleType.ThemeColour,
            };

            string value = RuleValueTextBox.Text?.Trim() ?? string.Empty;

            // THEME_COLOUR 的值必须是 true / false，其它输入一律按 false（忽略 theme-color）处理
            if (type == AdaptiveRuleType.ThemeColour)
            {
                value = AdaptiveColourRuleTable.ParseThemeColourValue(value) ? "true" : "false";
            }

            AdaptiveRuleScheme scheme = RuleSchemeComboBox.SelectedIndex switch
            {
                1 => AdaptiveRuleScheme.Light,
                2 => AdaptiveRuleScheme.Dark,
                _ => AdaptiveRuleScheme.Both,
            };

            _rules.Add(new AdaptiveColourRule
            {
                Host = host,
                Type = type,
                Value = value,
                Scheme = scheme,
                BuiltIn = false
            });

            AdaptiveColourSettings.SaveRules(_rules);

            RuleHostTextBox.Text = string.Empty;
            RuleValueTextBox.Text = string.Empty;
            RuleEmptyHint.Visibility = Visibility.Collapsed;
        }

        private void OnDeleteRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: AdaptiveColourRule rule })
            {
                _rules.Remove(rule);
                AdaptiveColourSettings.SaveRules(_rules);
                RuleEmptyHint.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                AdaptiveColourSettings.Enabled = toggle.IsOn;
            }
        }

        private void OnDynamicToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                AdaptiveColourSettings.Dynamic = toggle.IsOn;
            }
        }

        private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
        {
            // AOT 修复：用 SelectedIndex，不做类型转换
            if (sender is ComboBox comboBox && comboBox.SelectedIndex >= 0)
            {
                AdaptiveColourSettings.Scheme = (AdaptiveColourSchemeMode)comboBox.SelectedIndex;
            }
        }

        private void OnNoThemeColourToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                AdaptiveColourSettings.NoThemeColour = toggle.IsOn;
            }
        }

        private void OnAllowDarkLightToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                AdaptiveColourSettings.AllowDarkLight = toggle.IsOn;
            }
        }

        private void OnTabBarChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsNaN(args.NewValue))
            {
                AdaptiveColourSettings.TabBar = args.NewValue;
            }
        }

        private void OnMinContrastLightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsNaN(args.NewValue))
            {
                AdaptiveColourSettings.MinContrastLight = args.NewValue;
            }
        }

        private void OnMinContrastDarkChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsNaN(args.NewValue))
            {
                AdaptiveColourSettings.MinContrastDark = args.NewValue;
            }
        }

        private void OnFallbackLightLostFocus(object sender, RoutedEventArgs e)
        {
            CommitColourText(FallbackLightTextBox, light: true);
        }

        private void OnFallbackDarkLostFocus(object sender, RoutedEventArgs e)
        {
            CommitColourText(FallbackDarkTextBox, light: false);
        }

        /// <summary>
        /// 兜底色只接受能解析的 CSS 颜色；解析不了就把输入框滚回当前设置值，
        /// 免得一个拼错的十六进制被写进存储、让页面取色一路用兜底色。
        /// </summary>
        private static void CommitColourText(TextBox textBox, bool light)
        {
            string text = textBox.Text?.Trim() ?? string.Empty;

            if (!AdaptiveColour.TryParse(text, out _))
            {
                textBox.Text = light ? AdaptiveColourSettings.FallbackLight : AdaptiveColourSettings.FallbackDark;
                return;
            }

            if (light)
            {
                AdaptiveColourSettings.FallbackLight = text;
            }
            else
            {
                AdaptiveColourSettings.FallbackDark = text;
            }
        }

        private void OnQueryLostFocus(object sender, RoutedEventArgs e)
        {
            AdaptiveColourSettings.Query = QueryTextBox.Text ?? string.Empty;
        }
    }
}
