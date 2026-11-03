namespace DockedTools.Features.MainWindowContent.ContentArea
{
    /// <summary>
    /// 页面导航感知接口，用于支持缓存页面的导航事件
    /// </summary>
    public interface INavigationAware
    {
        /// <summary>
        /// 当页面被导航到时调用（包括从缓存恢复）
        /// </summary>
        void OnNavigatedTo(object? parameter);

        /// <summary>
        /// 当页面被导航离开时调用
        /// </summary>
        void OnNavigatedFrom();
    }

    /// <summary>
    /// 页面返回接管接口：实现此接口的页面可拦截顶栏返回按钮点击。
    /// 返回 true 表示页面已处理，框架不再执行 GoBack；返回 false 则框架正常返回。
    /// </summary>
    public interface IBackHandler
    {
        /// <summary>
        /// 当用户点击返回按钮时调用
        /// </summary>
        /// <returns>true 表示页面已处理返回逻辑，框架不再执行 GoBack；false 表示框架继续执行默认返回</returns>
        bool OnBackRequested();
    }

    /// <summary>
    /// 关窗 / 出缓存时的彻底放手接口。
    ///
    /// 【要解决的问题】
    /// 本项目大量控件是「构造期订阅进程级静态事件 + Unloaded 里退订」的写法。
    /// 这条退订路径在主窗口关闭时靠不住：WinUI 3 的 Window 不会注销自己挂在 XAML/x:Bind
    /// 上的处理器（microsoft-ui-xaml issue #9960），窗口销毁那一拍 Unloaded 也常常整个跳过。
    /// 于是静态事件的委托列表里就一直留着这个实例 —— 一座内容区连着它最多 20 个缓存页
    /// 和各自的 WebView2，每开关一次窗口多一份，外在表现就是"窗口关了、内存没回来"。
    ///
    /// 【约定】
    /// 实现者负责把自己从【静态字段登记】和【静态事件订阅】里摘干净，并且必须幂等：
    /// Unloaded、关窗、出缓存这几条路都可能再走一遍同一段代码，重复执行不许抛、不许有副作用。
    /// 实例与实例之间的普通订阅（自己 ↔ 子控件）不用管，那种自引用环 GC 自己能收。
    ///
    /// 【调用点】
    /// · 主窗口 Closed → 链接器 → 导航栏 / 内容区（自上而下级联）
    /// · PageCacheManager.DisposeCachedPage → 出缓存的页面
    /// </summary>
    public interface ICloseReleasable
    {
        /// <summary>
        /// 断开本实例的全部静态登记与静态事件订阅，并释放它托管的重资源。幂等，可重复调用。
        /// </summary>
        void ReleaseOnClose();
    }
}
