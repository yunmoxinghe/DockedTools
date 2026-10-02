using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using DockedTools.Features.UnifiedCalls.TopAppBar;

namespace DockedTools.Features.Pages.AI
{
    public sealed partial class AIPage : Page
    {
        private readonly 智能标题 _智能标题 = new();

        public AIPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, AIScrollViewer, PageTitleBlock);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }
    }
}
