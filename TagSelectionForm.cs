using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public sealed class TagFontSetting
    {
        public string Field { get; set; }
        public string Family { get; set; }
        public float Size { get; set; }
        public int Style { get; set; }
    }

    internal sealed class TagSelectionForm : Form
    {
        private readonly CheckedListBox fieldsList;
        private readonly Dictionary<Plugin.MetaDataType, Font> fontOverrides = new Dictionary<Plugin.MetaDataType, Font>();
        private readonly Label fontStatus;

        public Plugin.MetaDataType[] SelectedFields
        {
            get
            {
                var selected = new List<Plugin.MetaDataType>();
                foreach (object item in fieldsList.CheckedItems)
                    selected.Add((Plugin.MetaDataType)item);
                return selected.ToArray();
            }
        }

        public Dictionary<Plugin.MetaDataType, Font> SelectedFonts
        {
            get
            {
                var selected = new Dictionary<Plugin.MetaDataType, Font>();
                foreach (var pair in fontOverrides)
                    selected.Add(pair.Key, new Font(pair.Value, pair.Value.Style));
                return selected;
            }
        }

        public TagSelectionForm(
            IEnumerable<Plugin.MetaDataType> fields,
            ISet<Plugin.MetaDataType> selectedFields,
            IDictionary<Plugin.MetaDataType, Font> selectedFonts)
        {
            Text = "PopUPAlbum info: choose tags";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(360, 420);
            Size = new Size(440, 620);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            var heading = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Select the tag fields to show for a selected track.",
                TextAlign = ContentAlignment.MiddleLeft
            };

            fieldsList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                Sorted = true
            };
            foreach (Plugin.MetaDataType field in fields)
                fieldsList.Items.Add(field, selectedFields.Contains(field));
            foreach (var pair in selectedFonts)
                fontOverrides[pair.Key] = new Font(pair.Value, pair.Value.Style);

            var fontPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            var fontButton = new Button { Text = "Font...", Width = 84 };
            var defaultFontButton = new Button { Text = "Use default", Width = 92 };
            fontStatus = new Label { AutoSize = true, Padding = new Padding(4, 8, 0, 0) };
            fontButton.Click += (s, e) => ChooseFontForSelectedField();
            defaultFontButton.Click += (s, e) => ClearSelectedFieldFont();
            fieldsList.SelectedIndexChanged += (s, e) => UpdateFontStatus();
            fontPanel.Controls.Add(fontButton);
            fontPanel.Controls.Add(defaultFontButton);
            fontPanel.Controls.Add(fontStatus);
            UpdateFontStatus();

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 76 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 76 };
            var clearButton = new Button { Text = "Clear all", Width = 76 };
            var selectAllButton = new Button { Text = "Select all", Width = 76 };
            clearButton.Click += (s, e) => SetAllChecked(false);
            selectAllButton.Click += (s, e) => SetAllChecked(true);
            footer.Controls.Add(okButton);
            footer.Controls.Add(cancelButton);
            footer.Controls.Add(clearButton);
            footer.Controls.Add(selectAllButton);

            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(fieldsList, 0, 1);
            layout.Controls.Add(fontPanel, 0, 2);
            layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private void SetAllChecked(bool isChecked)
        {
            for (int index = 0; index < fieldsList.Items.Count; index++)
                fieldsList.SetItemChecked(index, isChecked);
        }

        private void ChooseFontForSelectedField()
        {
            if (!(fieldsList.SelectedItem is Plugin.MetaDataType field))
                return;

            Font initialFont = fontOverrides.TryGetValue(field, out Font selectedFont)
                ? selectedFont
                : SystemFonts.MessageBoxFont;
            using (var dialog = new FontDialog { Font = initialFont, ShowEffects = false, FontMustExist = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                if (fontOverrides.TryGetValue(field, out Font previousFont))
                    previousFont.Dispose();
                fontOverrides[field] = new Font(dialog.Font, dialog.Font.Style);
            }
            UpdateFontStatus();
        }

        private void ClearSelectedFieldFont()
        {
            if (fieldsList.SelectedItem is Plugin.MetaDataType field && fontOverrides.TryGetValue(field, out Font font))
            {
                font.Dispose();
                fontOverrides.Remove(field);
            }
            UpdateFontStatus();
        }

        private void UpdateFontStatus()
        {
            if (fontStatus == null)
                return;

            if (fieldsList.SelectedItem is Plugin.MetaDataType field && fontOverrides.TryGetValue(field, out Font font))
                fontStatus.Text = field + ": " + font.Name;
            else if (fieldsList.SelectedItem is Plugin.MetaDataType selectedField)
                fontStatus.Text = selectedField + ": default font";
            else
                fontStatus.Text = "Select a tag to set its font";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Font font in fontOverrides.Values)
                    font.Dispose();
                fontOverrides.Clear();
            }
            base.Dispose(disposing);
        }
    }
}