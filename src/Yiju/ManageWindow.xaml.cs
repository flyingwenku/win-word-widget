using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yiju.Data;

namespace Yiju;

/// <summary>
/// 句子管理页：多列表 + 句子增删改查，逻辑与 Android 端 v1.2 一致。
///
/// 所有指向列表/句子的地方一律用 **Uuid**（理由见 SentenceStore 顶部说明）；
/// 显示的句子列表只取没删掉的，墓碑是给同步用的，用户不该看见。
/// </summary>
public partial class ManageWindow : Window
{
    private static ManageWindow _instance;

    private readonly SentenceStore _store;
    private CardConfig _cfg;

    /// <summary>管的是"某一张卡片"的设置（列表、模式、固定句），句子库本身是全局共用的</summary>
    public ManageWindow(CardConfig cfg, SentenceStore store)
    {
        InitializeComponent();

        _store = store;
        _cfg = cfg;

        CbMode.Items.Add(new ComboBoxItem { Content = "随机下一句", Tag = Mode.Random });
        CbMode.Items.Add(new ComboBoxItem { Content = "顺序下一句", Tag = Mode.Sequence });
        CbMode.Items.Add(new ComboBoxItem { Content = "每日一句", Tag = Mode.Daily });
        CbMode.Items.Add(new ComboBoxItem { Content = "固定这一句", Tag = Mode.Fixed });

        ReloadFromConfig();
    }

    /// <summary>只开一个管理页（点两次不该弹出两个）；换了卡片就切过去，不新开窗口</summary>
    public static void ShowOrActivate(CardConfig cfg, SentenceStore store)
    {
        if (_instance == null)
        {
            _instance = new ManageWindow(cfg, store);
            _instance.Closed += (_, __) => _instance = null;
            _instance.Show();
            return;
        }

        if (!ReferenceEquals(_instance._cfg, cfg)) _instance.SwitchTo(cfg);

        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

    private void SwitchTo(CardConfig cfg)
    {
        _cfg = cfg;
        ReloadFromConfig();
    }

    private void ReloadFromConfig()
    {
        foreach (ComboBoxItem item in CbMode.Items)
        {
            if (item.Tag is Mode m && m == _cfg.Mode) { CbMode.SelectedItem = item; break; }
        }
        if (CbMode.SelectedItem == null) CbMode.SelectedIndex = 0;

        RefreshLists();
    }

    private SentenceList CurrentList => CbLists.SelectedItem as SentenceList ?? _store.First();

    // ===== 刷新 =====

    private void RefreshLists()
    {
        var keep = _cfg.ListUuid;

        CbLists.SelectionChanged -= CbLists_SelectionChanged;
        CbLists.ItemsSource = _store.Lists.ToList();

        var target = (!string.IsNullOrEmpty(keep) ? _store.Get(keep) : null) ?? _store.First();
        CbLists.SelectedItem = target;

        CbLists.SelectionChanged += CbLists_SelectionChanged;

        RefreshItems();
    }

    private void RefreshItems()
    {
        // 只显示没删掉的（墓碑是给同步用的，用户不该看见）
        LbItems.ItemsSource = CurrentList?.Alive() ?? new List<Sentence>();
        RefreshFixed();
    }

    private void RefreshFixed()
    {
        var items = CurrentList?.Alive() ?? new List<Sentence>();
        CbFixed.ItemsSource = items;
        CbFixed.SelectedItem = items.FirstOrDefault(i => i.Uuid == _cfg.FixedUuid) ?? items.FirstOrDefault();
        CbFixed.Visibility = _cfg.Mode == Mode.Fixed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 顶部：列表与模式 =====

    private void CbLists_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbLists.SelectedItem is not SentenceList list) return;
        _cfg.ListUuid = list.Uuid;
        _cfg.Save();
        RefreshItems();
        CardWindow.RefreshAll();
    }

    private void CbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbMode.SelectedItem is not ComboBoxItem item || item.Tag is not Mode mode) return;
        _cfg.Mode = mode;
        _cfg.Save();
        RefreshFixed();
        CardWindow.RefreshAll();
    }

    private void CbFixed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbFixed.SelectedItem is not Sentence s) return;
        _cfg.FixedUuid = s.Uuid;
        _cfg.Save();
        CardWindow.RefreshAll();
    }

    private void BtnLists_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var newItem = new MenuItem { Header = "新建列表" };
        newItem.Click += (_, __) => NewList();
        menu.Items.Add(newItem);

        var renameItem = new MenuItem { Header = "重命名当前列表" };
        renameItem.Click += (_, __) => RenameList();
        menu.Items.Add(renameItem);

        var delItem = new MenuItem { Header = "删除当前列表" };
        delItem.Click += (_, __) => DeleteList();
        menu.Items.Add(delItem);

        menu.PlacementTarget = BtnLists;
        menu.IsOpen = true;
    }

    private void NewList()
    {
        var (ok, name, _) = PromptWindow.Show(this, "新建列表", "列表名", "", null, null);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;

        var list = _store.AddList(name);
        _cfg.ListUuid = list.Uuid;
        _cfg.Save();
        RefreshLists();
    }

    private void RenameList()
    {
        var list = CurrentList;
        if (list == null) return;

        var (ok, name, _) = PromptWindow.Show(this, "重命名列表", "列表名", list.Name, null, null);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;

        _store.RenameList(list.Uuid, name);
        RefreshLists();
    }

    private void DeleteList()
    {
        var list = CurrentList;
        if (list == null) return;

        if (_store.Lists.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一个列表（卡片总要有个数据源）。", "一句");
            return;
        }

        if (MessageBox.Show(this, $"删除列表「{list.Name}」？里面的句子一起删掉。", "一句",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _store.DeleteList(list.Uuid);
        _cfg.ListUuid = _store.First()?.Uuid ?? "";
        _cfg.Save();
        RefreshLists();
        CardWindow.RefreshAll();
    }

    // ===== 句子 =====

    private void BtnAdd_Click(object sender, RoutedEventArgs e) => Add();

    private void TbNew_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add();
    }

    private void Add()
    {
        var text = TbNew.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "句子不能为空。", "一句");
            return;
        }

        _store.AddSentence(CurrentList?.Uuid ?? "", text, TbNewSource.Text);
        TbNew.Clear();
        TbNewSource.Clear();
        TbNew.Focus();
        RefreshItems();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var uuid = (string)((Button)sender).Tag;
        var s = CurrentList?.FindAlive(uuid);
        if (s == null) return;

        var (ok, text, source) = PromptWindow.Show(this, "编辑", "句子", s.Text, "出处（可留空）", s.Source);
        if (!ok || string.IsNullOrWhiteSpace(text)) return;

        _store.UpdateSentence(CurrentList.Uuid, uuid, text, source);
        RefreshItems();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var uuid = (string)((Button)sender).Tag;
        var s = CurrentList?.FindAlive(uuid);
        if (s == null) return;

        if (MessageBox.Show(this, "删除这一句？删了就找不回来了。", "一句",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _store.DeleteSentence(CurrentList.Uuid, uuid);
        RefreshItems();
    }
}
