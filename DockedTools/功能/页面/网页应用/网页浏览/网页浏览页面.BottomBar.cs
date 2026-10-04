using Microsoft.UI.Xaml;
using System;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Browser.Services;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - Reactor底部按钮栏模块
    /// 包含底部按钮栏的初始化、布局更新、状态管理逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private void InitializeBottomBarReactor()
        {
            // 重新挂载即代表旧的布局结果失效，清掉去抖基准，让下一次 UpdateBottomBarLayout 必定生效
            _lastBottomBarHostWidth = double.NaN;
            _lastAppliedButtonWidth = double.NaN;

            // ✅ 注册底部栏主题服务
            //   第二个参数是 XAML 上 BottomBarHost.Background 用的 ThemeResource 键，
            //   复位（网页色还原、Unregister 前）时按它把默认画刷取回来 —— ClearValue 回不去 ThemeResource。
            BottomBarThemeService.Register(BottomBarHost, "ApplicationPageBackgroundThemeBrush");

            // 创建 ReactorHostControl（WinUI ContentControl）
            _reactorHostControl = new Microsoft.UI.Reactor.Hosting.ReactorHostControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,  // 拉伸填充
                VerticalAlignment = VerticalAlignment.Stretch       // 拉伸填充
            };

            // 创建 Reactor 组件实例
            _bottomButtonBarComponent = new Components.BottomButtonBar
            {
                ButtonWidth = 48.0,  // 初始按钮宽度（会根据窗口自适应）
                CanGoBack = false,
                CanGoForward = false,
                OnBackClick = () => BackButton_Click(null!, null!),
                OnForwardClick = () => ForwardButton_Click(null!, null!),
                OnRefreshClick = () => RefreshButton_Click(null!, null!),
                OnCopyUrlClick = () => CopyUrlButton_Click(null!, null!),
                OnOpenExternalClick = () => OpenExternalButton_Click(null!, null!),
                IdleMode = ExperimentalSettings.IdlePowerMode,
                OnCycleIdleModeClick = OnCycleIdleModeClick,
                // 初始值刻意这样写：设置可能已经是开的（上次存的），但此刻内核一行都还没下发，
                // 「生效」必须是 false —— 按钮会先显示成待生效那一档，等 EnsurePwaModeAsync
                // 真正跑完再转绿。反过来就回到「首次进 Page 显示已开启、其实没开」的老毛病了。
                PwaMode = ExperimentalSettings.EnablePwaMode,
                PwaModeEffective = false,
                OnPwaModeClick = OnPwaModeButtonClick
            };

            // 挂载组件到 ReactorHostControl
            _reactorHostControl.Mount(_bottomButtonBarComponent);

            // 将 ReactorHostControl 添加到容器
            BottomButtonsContainer.Children.Add(_reactorHostControl);

            // 宽度动画的驱动：每帧把新宽度写回组件并重新 Mount。
            // 初值必须与上面组件的 ButtonWidth 一致，否则第一次布局会被当成一次「跳变」而滑一下。
            _bottomBarWidthTransition = new Services.BottomBarWidthTransition(
                DispatcherQueue,
                width =>
                {
                    if (_bottomButtonBarComponent is null)
                    {
                        return;
                    }

                    _bottomButtonBarComponent.ButtonWidth = width;
                    _reactorHostControl?.Mount(_bottomButtonBarComponent);

                    // ⭐ 回填【实际下发】的宽度，而不是「上次想给的宽度」。
                    // 动画中途被 Stop()（页面被淘汰）时真正生效的是插值到的那个中间值，
                    // 若这里记的是目标值，下次布局的去抖就会以为「已经下发过了」而跳过，
                    // 六个按钮的宽度从此永久卡在一个非法的中间值上，再也纠正不回来。
                    _lastAppliedButtonWidth = width;
                },
                _bottomButtonBarComponent.ButtonWidth);

            // ⭐ 建好立刻对齐一次 PWA 状态。
            //    下发挂在 WebView 初始化那条链上，通常比底栏更早跑完 —— 彼时
            //    SetPwaModeEffective 会因为组件还是 null 而整个空转。
            //    不补这一枪，图标就一直停在上面写的「待生效」那一档，
            //    要等下一次导航事件碰巧把它刷绿。
            UpdateBottomBarPwaMode();
        }

        /// <summary>
        /// 入场动画播放前先把底栏按钮宽度测出来。
        ///
        /// <para>为什么需要它：宽度计算的唯一入口是 <c>BottomBarHost.SizeChanged</c>，
        /// 而 SizeChanged 是<b>布局 pass 完成之后</b>才发的 —— 也就是说新建的页面
        /// 必然先用组件构造时那个拍脑袋的 48px 画出第一帧，动画都播了一半才收到通知。
        /// 观众看到的就是「底栏按钮先挤在一块（或撑得很开），动画结束时啪地跳到正确位置」。</para>
        ///
        /// <para>这里在 Loaded 里补一次同步测量：元素此时已在视觉树中，
        /// 若布局还没跑过（<c>ActualWidth</c> 仍为 0）就 <c>UpdateLayout()</c> 强制跑一次，
        /// 于是入场动画的第一帧底栏已经是真宽度。</para>
        ///
        /// <para>已经算过（<c>_lastAppliedButtonWidth</c> 不是 NaN）就什么都不做 ——
        /// 切回缓存页时宽度没变、不该重算。</para>
        /// </summary>
        private void MeasureBottomBarBeforeFirstFrame()
        {
            if (_bottomButtonBarComponent is null || !double.IsNaN(_lastAppliedButtonWidth))
            {
                return;
            }

            // 布局已经跑过（比如页面是复用挂回来的）就不必强制 UpdateLayout，
            // 那会让整棵子树再走一遍 measure/arrange，白烧一次。
            if (BottomBarHost.ActualWidth <= 0)
            {
                // Loaded 有可能正处在某次布局 pass 中间，这时的 UpdateLayout 属于自找麻烦；
                // 失败了不算问题 —— 宽度随后会由 SizeChanged 补上，只是那一发会走动画。
                try
                {
                    BottomBarHost.UpdateLayout();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WebBrowserPage] 首帧前测量底栏宽度失败，交给 SizeChanged 兜底: {ex.Message}");
                    return;
                }
            }

            UpdateBottomBarLayout();
        }

        /// <summary>
        /// 标记「页面已经画出来了」。此后才测到的首个宽度要走动画，不能直接落值。
        /// </summary>
        private void MarkBottomBarFirstFrameRendered()
        {
            _bottomBarFirstFrameRendered = true;
        }

        /// <summary>
        /// 按宿主可用宽度重算按钮宽度并下发。
        ///
        /// ⭐ 入口挂着 BottomBarHost.SizeChanged，而 SizeChanged 在**布局动画、窗口拖拽、
        /// 多次重测量**期间会连续多帧触发；每下发一次宽度就是一次 Mount()，
        /// 也就是一次完整 reconcile。所以这里加了三道数值去抖，全部不含时间延迟 ——
        /// 目的是「别白干」，而不是「晚半拍」。
        ///
        /// <para>末尾的下发交给 <see cref="Services.BottomBarWidthTransition"/>：
        /// 它在离散跳变时会额外按帧下发若干次，所以这里的去抖管的是「要不要重新计算」，
        /// 每帧成本则由驱动那边的阈值兜着。</para>
        /// </summary>
        private void UpdateBottomBarLayout()
        {
            if (BottomBarHost.ActualWidth <= 0 || _bottomButtonBarComponent == null)
            {
                return;
            }

            const int buttonCount = 7;  // 5 个功能按钮 + 1 个「收起时模式」+ 1 个「PWA 模式」
            const double minButtonWidth = 40.0;
            const double maxButtonWidth = 68.0;
            const double fixedHorizontalSpacing = 4.0;  // 固定左右和按钮间距

            double availableWidth = BottomBarHost.ActualWidth;

            // ① 宿主宽度去抖：小于 1px 的抖动对人类视觉没有意义
            if (!double.IsNaN(_lastBottomBarHostWidth) &&
                Math.Abs(availableWidth - _lastBottomBarHostWidth) < 1.0)
            {
                return;
            }
            _lastBottomBarHostWidth = availableWidth;

            // 总间距 = 左边距 + (按钮数-1)*按钮间距 + 右边距 = fixedHorizontalSpacing * (buttonCount + 1)
            double totalSpacing = fixedHorizontalSpacing * (buttonCount + 1);

            // ② 布局中间态保护：这是「间距回退到默认」的直接来源。
            //    窗口状态动画 / 重测量过程中宿主宽度会短暂缩到很小，此时 clamp 会把按钮压到
            //    minButtonWidth；而最后一帧无论停在哪个瞬时值都会成为最终外观。
            //    既然连最小宽度的按钮都装不下，说明这就是中间态 —— 保持上一次的计算结果不动。
            //
            //    ⚠️ 这里的坑在于「不动」是建立在「已经算过一次」之上的：
            //    于是**窄窗口（<268px）永远走不到下面的赋值**，按钮宽度一直停在构造时的 48px
            //    并横向溢出容器 —— 越窄越错，且看不出是这里的阈值而不是 Reactor 的问题。
            //    所以只有「已经算过一次」才谈得上「保持上一次不动」；一次都没算过时退化为
            //    按最小宽度做一次尽力布局，宁可挤也不让它停在凭空写的初始值上。
            double minRequiredWidth = totalSpacing + minButtonWidth * buttonCount;
            if (availableWidth < minRequiredWidth && !double.IsNaN(_lastAppliedButtonWidth))
            {
                return;
            }

            double widthForButtons = availableWidth - totalSpacing;
            double buttonWidth = widthForButtons / buttonCount;

            // 限制按钮宽度在最小和最大值之间
            buttonWidth = Math.Max(minButtonWidth, Math.Min(maxButtonWidth, buttonWidth));

            // 同步最新模式（可能被其他页面的实例改过）。
            // 放在宽度去抖之前：模式变化属于语义变化，必须能穿透去抖触发一次重渲染。
            WebViewIdlePowerMode currentMode = ExperimentalSettings.IdlePowerMode;
            bool idleModeChanged = _bottomButtonBarComponent.IdleMode != currentMode;
            _bottomButtonBarComponent.IdleMode = currentMode;

            // ③ 按钮宽度去抖：亚像素抖动不改变任何可见布局，不值得一次 reconcile
            if (!idleModeChanged &&
                !double.IsNaN(_lastAppliedButtonWidth) &&
                Math.Abs(buttonWidth - _lastAppliedButtonWidth) < 0.5)
            {
                return;
            }
            // ⚠️ 这里刻意【不】把 buttonWidth 记进 _lastAppliedButtonWidth ——
            // 那条记录由 _apply 回调回填实际下发值（见 InitializeBottomBarReactor）。
            // 在此处记目标值的话，动画中途被掐断时记录会和目标值不一致，
            // 去抖③就会误判「没变化」而把真正需要补的那一发吞掉。
            bool firstApply = double.IsNaN(_lastAppliedButtonWidth);

            // ④-a 首帧 + 还没画出来（Loaded 里那次同步测量）：直接落值。
            //     入场动画一帧未渲染，落真值等于「底栏一直就是这个宽度」，没有任何跳变可见 ——
            //     这是理想路径，也是 MeasureBottomBarBeforeFirstFrame 存在的全部意义。
            //
            // ④-b 首帧 + 已经画出来了（SizeChanged 迟到的兜底路径）：
            //     观众此刻看到的是组件构造时那个 48px，直接落值会在动画收尾时硬跳一下 ——
            //     「没创建过的 page 首次动画时底栏间距不对，动画结束才啪地跳好」就是它。
            //     这种时序改走动画：让按钮从旧位置平滑滑到新位置，跳变被展开成一段位移。
            //
            // ④-c 非首帧：交给驱动判断「该滑还是该跟」（判据见 BottomBarWidthTransition）。
            bool animateFirstApply = firstApply && _bottomBarFirstFrameRendered;

            if (firstApply && !animateFirstApply)
            {
                _bottomBarWidthTransition?.Set(buttonWidth);
            }
            else
            {
                _bottomBarWidthTransition?.AnimateTo(buttonWidth);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[UpdateBottomBarLayout] buttonWidth={buttonWidth:F2} (间距固定4px) " +
                $"{(firstApply ? (animateFirstApply ? "首帧→动画补" : "首帧→直接落") : "跟随")}");
        }

        /// <summary>
        /// 停掉底栏宽度动画（保持当前宽度，不回落）。
        ///
        /// <para>只在页面被真正拆除（<c>DisposeWebView</c>）时调用。
        /// ⚠️ 刻意<b>不在 Unloaded 里调</b>：Unloaded 在切页时也会触发，而页面随后会被缓存复用，
        /// 那时宿主尺寸可能完全没变、<c>SizeChanged</c> 不会再发一次，
        /// 半路掐断就会让按钮宽度永久停在插值的中间值上。</para>
        /// </summary>
        private void StopBottomBarWidthAnimation() => _bottomBarWidthTransition?.Stop();

        /// <summary>
        /// 更新底部导航按钮的启用/禁用状态
        /// </summary>
        private void UpdateNavigationButtonStates()
        {
            if (_bottomButtonBarComponent == null || _reactorHostControl == null)
            {
                return;
            }

            bool canGoBack = WebView?.CanGoBack ?? false;
            bool canGoForward = WebView?.CanGoForward ?? false;
            WebViewIdlePowerMode currentMode = ExperimentalSettings.IdlePowerMode;
            bool pwaMode = ExperimentalSettings.EnablePwaMode;
            bool pwaEffective = pwaMode && _pwaModeEffective;

            // ⭐ 脏值比对：这个函数在导航事件里会被频繁调用（HistoryChanged 等），
            //    而 props 大多没变。全部值都没变就不必走一次完整 reconcile。
            //    ⚠️ PwaMode / PwaModeEffective 必须一起比 —— 只比前者的话，
            //    「设置开、生效假」这一档会在下一次导航事件里被判成「没变化」而丢掉。
            if (_bottomButtonBarComponent.CanGoBack == canGoBack &&
                _bottomButtonBarComponent.CanGoForward == canGoForward &&
                _bottomButtonBarComponent.IdleMode == currentMode &&
                _bottomButtonBarComponent.PwaMode == pwaMode &&
                _bottomButtonBarComponent.PwaModeEffective == pwaEffective)
            {
                return;
            }

            // 更新组件 Props
            _bottomButtonBarComponent.CanGoBack = canGoBack;
            _bottomButtonBarComponent.CanGoForward = canGoForward;
            _bottomButtonBarComponent.IdleMode = currentMode;
            _bottomButtonBarComponent.PwaMode = pwaMode;
            _bottomButtonBarComponent.PwaModeEffective = pwaEffective;

            // 触发重新渲染
            _reactorHostControl.Mount(_bottomButtonBarComponent);

            System.Diagnostics.Debug.WriteLine($"[UpdateNavigationButtonStates] CanGoBack={canGoBack}, CanGoForward={canGoForward}, PwaMode={pwaMode}/{pwaEffective}");
        }

        /// <summary>
        /// 底部栏「收起时模式」按钮：普通 → 高效 → 挂起 → 普通
        /// 只改设置，实际生效发生在下次窗口/页面被收起时
        /// </summary>
        private void OnCycleIdleModeClick()
        {
            WebViewIdlePowerMode next = ExperimentalSettings.CycleIdlePowerMode();
            System.Diagnostics.Debug.WriteLine($"[OnCycleIdleModeClick] 窗口收起时模式 → {next}");

            if (_bottomButtonBarComponent != null)
            {
                _bottomButtonBarComponent.IdleMode = next;
                _reactorHostControl?.Mount(_bottomButtonBarComponent);
            }
        }
    }
}
