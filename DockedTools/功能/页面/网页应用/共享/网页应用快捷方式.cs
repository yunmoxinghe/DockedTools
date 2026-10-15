namespace DockedTools.Features.Pages.WebApp.Shared
{
    public sealed record WebAppShortcut(
        string Id, 
        string Name, 
        string Url, 
        byte[]? IconBytes,
        KeyboardMappingButtonConfig? LeftButtonConfig = null,
        KeyboardMappingButtonConfig? RightButtonConfig = null,
        bool NotificationsEnabled = true,
        bool GeolocationEnabled = true,
        bool CameraEnabled = true,
        bool MicrophoneEnabled = true,
        bool ClipboardReadEnabled = true)
    {
        /// <summary>
        /// 获取左侧按钮配置（如果为 null 则返回默认禁用配置）
        /// </summary>
        public KeyboardMappingButtonConfig LeftButton => LeftButtonConfig ?? KeyboardMappingButtonConfig.CreateDefault();

        /// <summary>
        /// 获取右侧按钮配置（如果为 null 则返回默认禁用配置）
        /// </summary>
        public KeyboardMappingButtonConfig RightButton => RightButtonConfig ?? KeyboardMappingButtonConfig.CreateDefault();
    }
}
