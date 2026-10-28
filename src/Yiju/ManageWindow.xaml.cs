using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yiju.Data;

namespace Yiju;

/// <summary>
/// 句子管理页：多列表 + 句子增删改查，逻辑与 Android 端 v1.2 一致。
/// </summary>
public partial class ManageWindow : Window
{
    private static ManageWindow _instance;

    private readonly CardWindow _card;
    private readonly SentenceStore _store;
    private readonly AppConfig _cfg;

    public ManageWindow(CardWindow card, SentenceStore store, AppConfig cfg)
    {
        InitializeComponent();

        _card = card;
        _store = store;
        _cfg = cfg;

        CbMode.Items.Add(new ComboBoxItem { Content = "随机下一句", Tag = Mode.Random });
        CbMode.Items.Add(new ComboBoxItem { Content = "顺序下一句", Tag = Mode.Sequence });
        CbMode.Items.Add(new ComboBoxItem { Content = "每日一句", Tag = Mode.Daily });
        CbMode.Items.Add(new ComboBoxItem { Content = "固定这一句", Tag = Mode.Fixed });

        foreach (ComboBoxItem item in CbMode.Items)
        {
            if (item.Tag is Mode m && m == _cfg.Mode) { CbMode.SelectedItem = item; break; }
        }
        if (CbMode.SelectedItem == null) CbMode.SelectedIndex = 0;

        RefreshLists();
    }

    /// <summary>只开一个管理页（点两次托盘菜单不该弹出两个）</summary>
    public static void ShowOrActivate(CardWindow card, SentenceStore store, AppConfig cfg)
    {
        if (_instance == null)
        {
            _instance = new ManageWindow(card, store, cfg);
            _instance.Closed += (_, __) => _instance = null;
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    private SentenceList CurrentList => CbLists.SelectedItem as SentenceList ?? _store.First();

    // ===== 刷新 =====

    private void RefreshLists()
    {
        var keep = _cfg.ListId;

        CbLists.SelectionChanged -= CbLists_SelectionChanged;
        CbLists.ItemsSource = _store.Lists.ToList();

        var target = _store.Get(keep) ?? _store.First();
        CbLists.SelectedItem = target;

        CbLists.SelectionChanged += CbLists_SelectionChanged;

        RefreshItems();
    }

    private void RefreshItems()
    {
        LbItems.ItemsSource = CurrentList?.Items.ToList() ?? new List<Sentence>();
        RefreshFixed();
    }

    private void RefreshFixed()
    {
        var items = CurrentList?.Items.ToList() ?? new List<Sentence>();
        CbFixed.ItemsSource = items;
        CbFixed.SelectedItem = items.FirstOrDefault(i => i.Id == _cfg.FixedId) ?? items.FirstOrDefault();
        CbFixed.Visibility = _cfg.Mode == Mode.Fixed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 顶部：列表与模式 =====

    private void CbLists_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbLists.SelectedItem is not SentenceList list) return;
        _cfg.ListId = list.Id;
        _cfg.Save();
        RefreshItems();
        _card?.Refresh();
    }

    private void CbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbMode.SelectedItem is not ComboBoxItem item || item.Tag is not Mode mode) return;
        _cfg.Mode = mode;
        _cfg.Save();
        RefreshFixed();
        _card?.Refresh();
    }

    private void CbFixed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbFixed.SelectedItem is not Sentence s) return;
        _cfg.FixedId = s.Id;
        _cfg.Save();
        _card?.Refresh();
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
        _cfg.ListId = list.Id;
        _cfg.Save();
        RefreshLists();
    }

    private void RenameList()
    {
        var list = CurrentList;
        if (list == null) return;

        var (ok, name, _) = PromptWindow.Show(this, "重命名列表", "列表名", list.Name, null, null);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;

        _store.RenameList(list.Id, name);
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

        _store.DeleteList(list.Id);
        _cfg.ListId = _store.First()?.Id ?? -1;
        _cfg.Save();
        RefreshLists();
        _card?.Refresh();
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

        _store.AddSentence(CurrentList?.Id ?? -1, text, TbNewSource.Text);
        TbNew.Clear();
        TbNewSource.Clear();
        TbNew.Focus();
        RefreshItems();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var id = (long)((Button)sender).Tag;
        var s = CurrentList?.Items.FirstOrDefault(i => i.Id == id);
        if (s == null) return;

        var (ok, text, source) = PromptWindow.Show(this, "编辑", "句子", s.Text, "出处（可留空）", s.Source);
        if (!ok || string.IsNullOrWhiteSpace(text)) return;

        _store.UpdateSentence(CurrentList.Id, id, text, source);
        RefreshItems();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = (long)((Button)sender).Tag;
        var s = CurrentList?.Items.FirstOrDefault(i => i.Id == id);
        if (s == null) return;

        if (MessageBox.Show(this, "删除这一句？删了就找不回来了。", "一句",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _store.DeleteSentence(CurrentList.Id, id);
        RefreshItems();
    }
}
