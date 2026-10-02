using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 键盘映射按钮UI模块
    /// 包含左右映射按钮的创建、图标设置、点击事件、快捷键发送等
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// 左侧映射按钮：下发【按钮数据】而不是 Button。
        /// 旧实现要在这里手搓 Width/Height/Padding/CornerRadius + 三套悬停色 ResourceDictionary，
        /// 现在外观由 AppTopBar 统一给出，页面只需要 Id（用于分派事件）、字形与提示。
        /// </summary>
        private void SetupLeftMappingButton()
        {
            if (_currentShortcut?.LeftButton is not { } config || !config.IsEnabled)
            {
                _leftMappingButton = null;
                TopAppBarService.UnregisterAction(LeftMappingButtonId);
                TopAppBarService.SetLeftButtons(null);
                return;
            }

            _leftMappingButton = BuildMappingButton(LeftMappingButtonId, config);

            TopAppBarService.RegisterAction(LeftMappingButtonId, () => OnLeftMappingButtonClick());
            TopAppBarService.SetLeftButtons(new[] { _leftMappingButton });
        }

        private void SetupRightMappingButton()
        {
            _rightMappingButton = null;

            if (_currentShortcut?.RightButton is not { } config || !config.IsEnabled)
            {
                TopAppBarService.UnregisterAction(RightMappingButtonId);
                return;
            }

            _rightMappingButton = BuildMappingButton(RightMappingButtonId, config);

            TopAppBarService.RegisterAction(RightMappingButtonId, () => OnRightMappingButtonClick());
        }

        /// <summary>
        /// 把一份映射按钮配置翻译成顶栏按钮【数据】。
        /// 静态 / 动画二选一走 <see cref="TopBarButton"/> 的两个工厂；动画那条路组件内部
        /// 会自己按指针按下/松开切 AnimatedIcon 的 Pressed / Normal 状态，
        /// 页面不再需要持有 AnimatedIcon 实例去手动播放。
        /// </summary>
        private static TopBarButton BuildMappingButton(string id, KeyboardMappingButtonConfig config)
        {
            var fallbackGlyph = string.IsNullOrWhiteSpace(config.StaticIconGlyph)
                ? "\uE8B5"
                : config.StaticIconGlyph;

            return string.Equals(config.IconType, "Animated", StringComparison.OrdinalIgnoreCase)
                ? TopBarButton.OfAnimated(id, config.AnimatedIconType, config.Tooltip, fallbackGlyph)
                : TopBarButton.Of(id, fallbackGlyph, config.Tooltip);
        }

        private async void OnLeftMappingButtonClick()
        {
            if (_currentShortcut == null || WebView?.CoreWebView2 == null)
            {
                return;
            }

            // 注：旧实现在这里手动播放 AnimatedIcon 的 Pressed→Normal；
            // 新顶栏自建的按钮没有 Page 可以碰的元素实例，按下反馈由 AppTopBar 自己提供。

            var config = _currentShortcut.LeftButton;
            await SendHotkeyToWebViewAsync(config.Key, config.Ctrl, config.Shift, config.Alt);

            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 左侧按钮发送快捷键: {config.GetHotkeyDisplayText()}");
        }

        private async void OnRightMappingButtonClick()
        {
            if (_currentShortcut == null || WebView?.CoreWebView2 == null)
            {
                return;
            }

            var config = _currentShortcut.RightButton;
            await SendHotkeyToWebViewAsync(config.Key, config.Ctrl, config.Shift, config.Alt);

            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 右侧按钮发送快捷键: {config.GetHotkeyDisplayText()}");
        }

        private async Task SendHotkeyToWebViewAsync(VirtualKey key, bool ctrl, bool shift, bool alt)
        {
            if (WebView?.CoreWebView2 == null || key == VirtualKey.None)
            {
                return;
            }

            try
            {
                // 构建修饰键字符串
                var modifiers = new System.Collections.Generic.List<string>();
                if (ctrl) modifiers.Add("ctrlKey: true");
                if (shift) modifiers.Add("shiftKey: true");
                if (alt) modifiers.Add("altKey: true");
                
                string modifiersStr = modifiers.Count > 0 ? ", " + string.Join(", ", modifiers) : "";
                
                // 使用 JavaScript 模拟键盘事件
                string script = $@"
                    (function() {{
                        const event = new KeyboardEvent('keydown', {{
                            key: '{GetKeyString(key)}',
                            code: '{GetKeyCode(key)}',
                            keyCode: {(int)key},
                            which: {(int)key},
                            bubbles: true,
                            cancelable: true{modifiersStr}
                        }});
                        document.dispatchEvent(event);
                        
                        const eventUp = new KeyboardEvent('keyup', {{
                            key: '{GetKeyString(key)}',
                            code: '{GetKeyCode(key)}',
                            keyCode: {(int)key},
                            which: {(int)key},
                            bubbles: true,
                            cancelable: true{modifiersStr}
                        }});
                        document.dispatchEvent(eventUp);
                        
                        return 'OK';
                    }})();
                ";

                await WebView.CoreWebView2.ExecuteScriptAsync(script);
                System.Diagnostics.Debug.WriteLine($"[SendHotkeyToWebViewAsync] 已发送快捷键");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SendHotkeyToWebViewAsync] 发送快捷键失败: {ex.Message}");
            }
        }

        private static string GetKeyString(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Enter => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Escape => "Escape",
                VirtualKey.Space => " ",
                VirtualKey.Back => "Backspace",
                VirtualKey.Delete => "Delete",
                VirtualKey.F5 => "F5",
                _ => key.ToString()
            };
        }

        private static string GetKeyCode(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Enter => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Escape => "Escape",
                VirtualKey.Space => "Space",
                VirtualKey.Back => "Backspace",
                VirtualKey.Delete => "Delete",
                VirtualKey.F5 => "F5",
                _ => $"Key{key}"
            };
        }
    }
}
