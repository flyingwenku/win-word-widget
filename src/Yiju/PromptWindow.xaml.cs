using System.Windows;

namespace Yiju;

public partial class PromptWindow : Window
{
    public string ValueA => TbA.Text;
    public string ValueB => TbB.Text;

    public PromptWindow(string title, string labelA, string valueA, string labelB, string valueB)
    {
        InitializeComponent();

        Title = title;
        LbA.Text = labelA;
        TbA.Text = valueA ?? "";

        if (labelB == null)
        {
            LbB.Visibility = Visibility.Collapsed;
            TbB.Visibility = Visibility.Collapsed;
        }
        else
        {
            LbB.Text = labelB;
            TbB.Text = valueB ?? "";
        }

        Loaded += (_, __) =>
        {
            TbA.Focus();
            TbA.CaretIndex = TbA.Text.Length;
        };
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>返回 (是否确定, 值A, 值B)。第二项传 null 表示只要一个输入框</summary>
    public static (bool Ok, string A, string B) Show(
        Window owner, string title, string labelA, string valueA, string labelB, string valueB)
    {
        var w = new PromptWindow(title, labelA, valueA, labelB, valueB) { Owner = owner };
        var ok = w.ShowDialog() == true;
        return (ok, w.ValueA, w.ValueB);
    }
}
