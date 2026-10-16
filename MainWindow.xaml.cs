using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GrimDawnModMerger
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<ModListItem> modList { get; set; }
        Merger merger;

        public MainWindow ()
        {
            InitializeComponent();

            modList = new ObservableCollection<ModListItem>();
            this.DataContext = this;
            merger = new Merger(UILogMessageHandler);

            string savedGameDir = Properties.Settings.Default.gameDir;
            if (!string.IsNullOrEmpty(savedGameDir))
                txtBoxGamePath.Text = savedGameDir;
        }

        private void btnUp_Click (object sender, RoutedEventArgs e)
        {
            MoveModInList(-1);
        }

        private void btnDown_Click (object sender, RoutedEventArgs e)
        {
            MoveModInList(1);
        }

        private void btnSelectPath_Click (object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                txtBoxGamePath.Text = dialog.FolderName;
            }
        }

        private void btnMerge_Click (object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtBoxMergeModName.Text))
                return;

            // get all active mods
            Trace.WriteLine("Btn merge clicked");
            List<string> activeMods = new List<string>();
            foreach (ModListItem item in modList)
            {
                if (item.IsSelected)
                {
                    activeMods.Add(item.Name);
                    Trace.WriteLine(item.Name + ", is checked: " + item.IsSelected);
                }
            }
            if (activeMods.Count == 0)
            {
                UILogMessageHandler(this, new UILogMessageEventArgs { Message = "Select at least one mod to merge." });
                return;
            }

            btnMerge.Content = "MERGE IN\nPROGRESS";
            btnMerge.IsEnabled = false;
            activeMods.Reverse();
            if (!merger.Merge(txtBoxMergeModName.Text, activeMods))
            {
                // merge was refused or could not start, give the button back
                btnMerge.Content = "Merge";
                btnMerge.IsEnabled = true;
            }
        }

        private void txtBoxGamePath_TextChanged (object sender, TextChangedEventArgs e)
        {
            // can fire while the window is still being constructed
            if (merger == null || modList == null)
                return;

            merger.GAME_DIR = txtBoxGamePath.Text;
            modList.Clear();

            // scan new directory for mods
            // TODO in future, save last configuration (isselected and position), as well as what mods were last merged
            string modsPath = Path.Combine(merger.GAME_DIR, "mods");
            if (!Directory.Exists(modsPath))
                return; // typing a path by hand, or the saved path no longer exists

            DirectoryInfo dirInfo = new DirectoryInfo(modsPath);
            foreach (DirectoryInfo d in dirInfo.GetDirectories())
            {
                if (d.Name.Equals("gdx1") || d.Name.Equals("gdx2"))
                    continue;
                modList.Add(new ModListItem(false, d.Name));
            }
            Properties.Settings.Default.gameDir = txtBoxGamePath.Text;
            Properties.Settings.Default.Save();
        }

        private void btnTest_Click (object sender, RoutedEventArgs e)
        {
        }

        public void UILogMessageHandler(object o, UILogMessageEventArgs e)
        {
            try // exception thrown when exiting, it is a problem with the cmd line thread exiting. TODO look into this
            {
                Dispatcher.Invoke(() =>
                {
                    if (e.Message.Contains("Parsing")) // just for fun, and a bit of clarity for the user
                    e.Message += "\nPacking Database...";

                    if (e.Message.Contains("Merge Complete"))
                    {
                        btnMerge.Content = "Merge";
                        btnMerge.IsEnabled = true;
                    }
                    if (txtBoxOutput.LineCount > 300)
                    {
                        var lines = (from item in txtBoxOutput.Text.Split('\n') select item.Trim());
                        lines = lines.Skip(200);
                        txtBoxOutput.Text = string.Join(Environment.NewLine, lines.ToArray());
                    }

                    txtBoxOutput.AppendText("\n" + e.Message);
                    txtBoxOutput.ScrollToEnd();
                //txtBoxOutput.Text += "\n" + e.Message;
            });
            } catch (Exception ex) { Trace.WriteLine(ex); }
        }

        private void Window_Closing (object sender, System.ComponentModel.CancelEventArgs e)
        {
            merger.Closing();
        }

        private void MoveModInList (int delta)
        {
            ModListItem item;
            int index;

            if (listBoxMods.SelectedItems.Count != 1)
                return;

            item = (ModListItem)listBoxMods.SelectedItems[0];
            index = modList.IndexOf(item);
            int newIndex = index + delta;
            if (index >= 0 && newIndex >= 0 && newIndex < modList.Count)
            {
                modList.Move(index, newIndex);
            }
        }

    }

    public class ModListItem
    {
        public string Name { get; set; }
        public bool IsSelected { get; set; }

        public ModListItem (bool isSelected, string name)
        {
            Name = name;
            IsSelected = isSelected;
        }
    }
}
