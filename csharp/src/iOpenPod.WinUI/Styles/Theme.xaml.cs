using Microsoft.UI.Xaml;

namespace IOpenPod.WinUI.Styles;

/// <summary>
/// 应用级资源字典。
///
/// 由于字典中包含使用 <c>x:Bind</c> 的 <c>DataTemplate</c>，编译器需要一个
/// 承载生成代码的类，因此这里提供一个最小的代码隐藏类型。
/// </summary>
public sealed partial class Theme : ResourceDictionary
{
    public Theme()
    {
        InitializeComponent();
    }
}
