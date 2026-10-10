using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace ITDatabaseSetup
{
    // A restrained desktop theme: system fonts, quiet surfaces and clear focus states.
    // The native controls still own keyboard navigation, automation and hit testing.
    public static class UiTheme
    {
        public static readonly Color Background=Color.FromArgb(245,245,247);
        public static readonly Color Surface=Color.White;
        public static readonly Color Text=Color.FromArgb(29,29,31);
        public static readonly Color Muted=Color.FromArgb(110,110,115);
        public static readonly Color Border=Color.FromArgb(220,220,224);
        public static readonly Color Blue=Color.FromArgb(0,112,224);
        public static readonly Color Selection=Color.FromArgb(229,240,255);
        public static readonly Color Green=Color.FromArgb(35,118,74);
        public static readonly Color Red=Color.FromArgb(180,45,45);

        public static void Apply(Form form)
        {
            form.BackColor=Background;
            form.ForeColor=Text;
            ApplyChildren(form);
        }
        static void ApplyChildren(Control parent)
        {
            foreach(Control control in parent.Controls){
                Button button=control as Button;
                if(button!=null && !(button is ModernButton)){
                    button.FlatStyle=FlatStyle.Flat;
                    button.FlatAppearance.BorderSize=1;
                    button.FlatAppearance.BorderColor=Border;
                    button.FlatAppearance.MouseOverBackColor=Color.FromArgb(240,244,249);
                    button.FlatAppearance.MouseDownBackColor=Selection;
                    button.UseVisualStyleBackColor=false;
                    button.BackColor=Surface;
                    button.ForeColor=Text;
                    button.Cursor=Cursors.Hand;
                }
                TextBox input=control as TextBox;
                if(input!=null){if(!input.ReadOnly || input.BorderStyle!=BorderStyle.None)input.BorderStyle=BorderStyle.FixedSingle;input.BackColor=Surface;input.ForeColor=Text;}
                NumericUpDown number=control as NumericUpDown;
                if(number!=null){number.BorderStyle=BorderStyle.FixedSingle;number.BackColor=Surface;number.ForeColor=Text;}
                if(control is Label && control.ForeColor==SystemColors.ControlText)control.ForeColor=Text;
                ApplyChildren(control);
            }
        }
        public static void ConfigureMenu(ContextMenuStrip menu)
        {
            menu.Font=new Font("Segoe UI",10);
            menu.ShowImageMargin=false;
            menu.Padding=new Padding(5);
            menu.RenderMode=ToolStripRenderMode.System;
            foreach(ToolStripItem item in menu.Items)item.Padding=new Padding(8,5,8,5);
        }
        public static void StyleTable(ListView list)
        {
            new TablePainter(list);
        }
        internal static GraphicsPath RoundedPath(RectangleF bounds,float radius)
        {
            GraphicsPath path=new GraphicsPath();
            float diameter=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));
            if(diameter<=0){path.AddRectangle(bounds);return path;}
            path.AddArc(bounds.X,bounds.Y,diameter,diameter,180,90);
            path.AddArc(bounds.Right-diameter,bounds.Y,diameter,diameter,270,90);
            path.AddArc(bounds.Right-diameter,bounds.Bottom-diameter,diameter,diameter,0,90);
            path.AddArc(bounds.X,bounds.Bottom-diameter,diameter,diameter,90,90);
            path.CloseFigure();return path;
        }

        sealed class TablePainter
        {
            readonly ListView list;
            readonly ImageList rows=new ImageList();
            readonly Bitmap spacer=new Bitmap(1,34);
            readonly Font heading;
            public TablePainter(ListView table)
            {
                list=table;list.OwnerDraw=true;list.GridLines=false;list.BorderStyle=BorderStyle.None;
                list.BackColor=Surface;list.ForeColor=Text;list.ShowItemToolTips=true;
                rows.ImageSize=new Size(1,34);rows.ColorDepth=ColorDepth.Depth32Bit;
                rows.Images.Add(spacer);
                list.SmallImageList=rows;
                heading=new Font(list.Font.FontFamily,list.Font.Size,FontStyle.Regular);
                list.DrawColumnHeader+=DrawHeader;list.DrawItem+=DrawItem;list.DrawSubItem+=DrawCell;
                list.Disposed+=(sender,args)=>{rows.Dispose();spacer.Dispose();heading.Dispose();};
            }
            void DrawHeader(object sender,DrawListViewColumnHeaderEventArgs e)
            {
                Color fill=SystemInformation.HighContrast?SystemColors.Control:Color.FromArgb(249,249,251);
                using(Brush brush=new SolidBrush(fill))e.Graphics.FillRectangle(brush,e.Bounds);
                using(Pen pen=new Pen(SystemInformation.HighContrast?SystemColors.WindowText:Border))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
                Rectangle text=e.Bounds;text.Inflate(-10,0);
                TextRenderer.DrawText(e.Graphics,e.Header.Text,heading,text,SystemInformation.HighContrast?SystemColors.ControlText:Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            }
            void DrawItem(object sender,DrawListViewItemEventArgs e)
            {
                // In Details mode each subitem paints its own background and text.
                if(list.View!=View.Details)e.DrawDefault=true;
            }
            void DrawCell(object sender,DrawListViewSubItemEventArgs e)
            {
                bool selected=e.Item.Selected;
                Color fill=selected?Selection:Surface;
                Color foreground=Text;
                if(SystemInformation.HighContrast){fill=selected?SystemColors.Highlight:SystemColors.Window;foreground=selected?SystemColors.HighlightText:SystemColors.WindowText;}
                else if(e.Header.Text=="Status" && e.Item.ForeColor!=Color.Empty && e.Item.ForeColor!=SystemColors.WindowText)foreground=e.Item.ForeColor;
                else if(e.ColumnIndex==3 && e.Item.Tag is Entry && ((Entry)e.Item.Tag).backup==null)foreground=Red;
                else if(e.Header.Text=="Backed up" && (e.SubItem.Text=="Never" || e.SubItem.Text.IndexOf("missing",StringComparison.OrdinalIgnoreCase)>=0))foreground=Muted;
                using(Brush brush=new SolidBrush(fill))e.Graphics.FillRectangle(brush,e.Bounds);
                Rectangle text=e.Bounds;text.Inflate(-10,0);
                if(e.ColumnIndex==0 && list.CheckBoxes){
                    Size glyph=Application.RenderWithVisualStyles?CheckBoxRenderer.GetGlyphSize(e.Graphics,CheckBoxState.UncheckedNormal):new Size(13,13);
                    Point location=new Point(e.Bounds.Left+4,e.Bounds.Top+(e.Bounds.Height-glyph.Height)/2);
                    if(Application.RenderWithVisualStyles)CheckBoxRenderer.DrawCheckBox(e.Graphics,location,e.Item.Checked?CheckBoxState.CheckedNormal:CheckBoxState.UncheckedNormal);
                    else ControlPaint.DrawCheckBox(e.Graphics,new Rectangle(location,glyph),e.Item.Checked?ButtonState.Checked:ButtonState.Normal);
                    text.X=e.Bounds.Left+26;text.Width=Math.Max(0,e.Bounds.Right-text.X-10);
                }
                TextRenderer.DrawText(e.Graphics,e.SubItem.Text,list.Font,text,foreground,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
                if(!selected)using(Pen pen=new Pen(SystemInformation.HighContrast?SystemColors.Window:Color.FromArgb(242,242,245)))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
                if(e.Item.Focused && list.Focused && e.ColumnIndex==0)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(e.Bounds,-1,-2),foreground,fill);
            }
        }
    }

    public sealed class RoundedCard : Panel
    {
        public int CornerRadius {get;set;}
        public Color BorderColor {get;set;}
        public RoundedCard()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            BackColor=UiTheme.Surface;CornerRadius=12;BorderColor=UiTheme.Border;
            Padding=new Padding(16);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent==null?UiTheme.Background:Parent.BackColor);
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(GraphicsPath path=UiTheme.RoundedPath(new RectangleF(0.5f,0.5f,Width-1,Height-1),CornerRadius)){
                using(Brush brush=new SolidBrush(BackColor))e.Graphics.FillPath(brush,path);
                using(Pen pen=new Pen(BorderColor))e.Graphics.DrawPath(pen,path);
            }
        }
    }

    public sealed class ModernButton : Button
    {
        bool primary,hover,pressed;
        public bool Primary {get{return primary;}set{primary=value;Invalidate();}}
        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
            Cursor=Cursors.Hand;Padding=new Padding(12,0,12,0);
        }
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)pressed=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space)pressed=true;Invalidate();base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;Invalidate();base.OnKeyUp(e);}
        protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill=Primary?UiTheme.Blue:UiTheme.Surface;
            Color foreground=Primary?Color.White:UiTheme.Text;
            if(!Enabled){fill=Color.FromArgb(234,234,238);foreground=Color.FromArgb(151,151,157);}
            else if(pressed)fill=Primary?Color.FromArgb(0,85,179):Color.FromArgb(225,232,241);
            else if(hover)fill=Primary?Color.FromArgb(0,100,208):Color.FromArgb(243,246,250);
            if(SystemInformation.HighContrast){fill=Primary?SystemColors.Highlight:SystemColors.Control;foreground=Enabled?(Primary?SystemColors.HighlightText:SystemColors.ControlText):SystemColors.GrayText;}
            e.Graphics.Clear(Parent==null?UiTheme.Background:Parent.BackColor);
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(GraphicsPath path=UiTheme.RoundedPath(new RectangleF(0.5f,0.5f,Width-1,Height-1),8)){
                using(Brush brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);
                using(Pen pen=new Pen(Primary && Enabled?fill:UiTheme.Border))e.Graphics.DrawPath(pen,path);
            }
            Rectangle text=ClientRectangle;text.Inflate(-8,-2);
            TextFormatFlags flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis;
            if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics,Text,Font,text,foreground,flags);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),foreground,fill);
        }
    }

    public sealed class BackupPickerForm : Form
    {
        public DatabaseBackup RestoreSelection {get;private set;}
        public BackupPickerForm(Engine engine,Connection connection,string database,string game)
        {
            Text="Restore backup";ClientSize=new Size(820,390);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            var title=new Label {Text="Restore "+game,Font=new Font("Segoe UI",18),AutoEllipsis=true};title.SetBounds(24,20,772,40);Controls.Add(title);
            var hint=new Label {Text="Choose a saved version. Initial database is the original archive from the first install.",ForeColor=UiTheme.Muted};hint.SetBounds(26,69,770,45);Controls.Add(hint);
            var versions=BackupHistoryControls.Table();versions.SetBounds(24,120,772,195);Controls.Add(versions);
            BackupHistoryControls.Load(versions,engine,connection,database);
            var restore=new ModernButton {Text="Restore backup…",Primary=true,Enabled=false};restore.SetBounds(626,335,170,36);Controls.Add(restore);
            Action choose=()=>{if(versions.SelectedItems.Count==1){RestoreSelection=(DatabaseBackup)versions.SelectedItems[0].Tag;DialogResult=DialogResult.OK;Close();}};
            versions.SelectedIndexChanged+=(s,e)=>restore.Enabled=versions.SelectedItems.Count==1 && File.Exists(engine.BackupRecordPath((DatabaseBackup)versions.SelectedItems[0].Tag));
            restore.Click+=(s,e)=>choose();AcceptButton=restore;
            var cancel=new ModernButton {Text="Cancel",DialogResult=DialogResult.Cancel};cancel.SetBounds(490,335,120,36);Controls.Add(cancel);CancelButton=cancel;
            UiTheme.Apply(this);
        }
    }
    public static class BackupHistoryControls
    {
        public static ListView Table()
        {
            var table=new ListView {View=View.Details,FullRowSelect=true,HideSelection=false,MultiSelect=false,AccessibleName="Backup history",ShowItemToolTips=true};
            table.Columns.Add("Date",173);table.Columns.Add("Type",130);table.Columns.Add("Availability",104);table.Columns.Add("Size",88);table.Columns.Add("File",265);UiTheme.StyleTable(table);return table;
        }
        public static void Load(ListView table,Engine engine,Connection connection,string database)
        {
            table.Items.Clear();
            foreach(var item in engine.BackupHistory(connection,database)){
                var row=new ListViewItem(DatabasePropertiesForm.LocalDate(item.createdUtc)){Tag=item};
                row.SubItems.Add(item.kind=="initial"?"Initial database":"Backup");row.SubItems.Add(File.Exists(engine.BackupRecordPath(item))?"Available":"Missing file");row.SubItems.Add(DatabasePropertiesForm.FileSize(item.bytes));row.SubItems.Add(Path.GetFileName(item.file));row.ToolTipText=(item.kind=="initial"?"Original archive copied after the first successful install.\r\n":"Installed database snapshot.\r\n")+engine.BackupRecordPath(item);table.Items.Add(row);
            }
        }
        public static void Attach(Form owner,ListView table,Engine engine,Connection connection,string database,bool canRestore,Action<DatabaseBackup> restore,Action changed)
        {
            var menu=new ContextMenuStrip();var restoreItem=new ToolStripMenuItem("Restore backup…");var deleteItem=new ToolStripMenuItem("Delete backup…"){ForeColor=Color.Firebrick};menu.Items.Add(restoreItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(deleteItem);UiTheme.ConfigureMenu(menu);table.ContextMenuStrip=menu;
            table.MouseDown+=(s,e)=>{if(e.Button!=MouseButtons.Right)return;var hit=table.HitTest(e.Location);table.SelectedItems.Clear();if(hit.Item!=null){hit.Item.Selected=true;hit.Item.Focused=true;}};
            menu.Opening+=(s,e)=>{e.Cancel=table.SelectedItems.Count!=1;if(!e.Cancel)restoreItem.Enabled=canRestore && File.Exists(engine.BackupRecordPath((DatabaseBackup)table.SelectedItems[0].Tag));};
            restoreItem.Click+=(s,e)=>{if(table.SelectedItems.Count==1)restore((DatabaseBackup)table.SelectedItems[0].Tag);};
            deleteItem.Click+=(s,e)=>{
                if(table.SelectedItems.Count!=1)return;var record=(DatabaseBackup)table.SelectedItems[0].Tag;
                if(MessageBox.Show(owner,"Delete this "+(record.kind=="initial"?"initial database archive":"backup")+" permanently?\r\n\r\n"+engine.BackupRecordPath(record)+"\r\n\r\nThe installed database will be kept.","Delete backup?",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                try{engine.DeleteRecordedBackup(connection,database,record);Load(table,engine,connection,database);changed();}catch(Exception ex){MessageBox.Show(owner,ex.Message,"Backup deletion stopped",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            };
            owner.FormClosed+=(s,e)=>menu.Dispose();
        }
    }
    public sealed class DatabasePropertiesForm : Form
    {
        public DatabaseBackup RestoreSelection {get;private set;}
        public bool HistoryChanged {get;private set;}
        public DatabasePropertiesForm(Engine engine,Connection connection,Entry entry,bool verified=true,bool canRestore=true)
        {
            if(entry==null)throw new ArgumentNullException("entry");
            Text="Database properties";ClientSize=new Size(800,715);MinimumSize=new Size(816,754);
            StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            var title=new Label {Text=entry.game,Font=new Font("Segoe UI",21,FontStyle.Regular),AutoEllipsis=true};title.SetBounds(24,18,752,42);Controls.Add(title);
            var subtitle=new Label {Text=(entry.database??entry.requestedDatabase)+"  ·  "+connection.server+":"+connection.port+(verified?"":"  ·  Connection not checked"),ForeColor=UiTheme.Muted};subtitle.SetBounds(26,65,748,25);Controls.Add(subtitle);
            var details=new RoundedCard();details.SetBounds(24,101,752,332);details.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(details);
            DatabaseBackup[] history=engine.BackupHistory(connection,entry.requestedDatabase);
            DatabaseBackup latest=history.FirstOrDefault();
            Game game=engine.catalog.FirstOrDefault(item=>item.database==entry.requestedDatabase);
            string status=!verified?"Connection not checked":entry.databaseNeedsRepair?"Needs repair":entry.databaseInstalled?"Installed":entry.backup!=null?"Ready to install":"Database file missing";
            string encoding=!verified?"Connection not checked":game==null?"Unknown":game.encoding+" (catalog default)";
            string source=entry.backup??(game==null?null:game.databaseFile);
            if(String.IsNullOrEmpty(source))source=game==null?"No source file selected":engine.BackupDirectory+Engine.ExpectedBackupPath(game)+" (expected)";
            string saved=latest==null?"No backup recorded":engine.BackupRecordPath(latest);
            bool exists=latest!=null && File.Exists(saved);
            AddDetail(details,"Status",status,15,28);
            AddDetail(details,"Encoding",encoding,47,28);
            AddDetail(details,"Install source",source,79,51);
            AddDetail(details,"Backed up",latest==null?"Never":LocalDate(latest.createdUtc),134,28);
            AddDetail(details,"Backup file",saved,166,51);
            AddDetail(details,"Availability",latest==null?"No backup yet":exists?"File available":"Recorded file is missing",221,28);
            AddDetail(details,"Backup size",latest==null?"—":FileSize(latest.bytes),253,28);
            AddDetail(details,"History",history.Length+" recorded version"+(history.Length==1?"":"s")+" on this connection",285,28);
            var historyLabel=new Label {Text="Backup history",Font=new Font("Segoe UI",11,FontStyle.Bold)};historyLabel.SetBounds(26,449,748,26);Controls.Add(historyLabel);
            var versions=BackupHistoryControls.Table();versions.SetBounds(24,481,752,154);versions.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
            BackupHistoryControls.Load(versions,engine,connection,entry.requestedDatabase);
            BackupHistoryControls.Attach(this,versions,engine,connection,entry.requestedDatabase,verified && canRestore,record=>{RestoreSelection=record;DialogResult=DialogResult.OK;Close();},()=>{
                HistoryChanged=true;var remaining=engine.BackupHistory(connection,entry.requestedDatabase);var newest=remaining.FirstOrDefault();
                foreach(TextBox field in details.Controls.OfType<TextBox>()){
                    if(field.AccessibleName=="History")field.Text=remaining.Length+" recorded versions on this connection";
                    if(field.AccessibleName=="Backed up")field.Text=newest==null?"Never":LocalDate(newest.createdUtc);
                    if(field.AccessibleName=="Backup file")field.Text=newest==null?"No backup recorded":engine.BackupRecordPath(newest);
                    if(field.AccessibleName=="Availability")field.Text=newest==null?"No backup yet":File.Exists(engine.BackupRecordPath(newest))?"File available":"Recorded file is missing";
                    if(field.AccessibleName=="Backup size")field.Text=newest==null?"—":FileSize(newest.bytes);
                }
            });
            Controls.Add(versions);
            var hint=new Label {Text=verified?"Dates use your local time. Backup history belongs to this server and port.":"Refresh to check this connection. Database results may be stale; backup history belongs to the displayed server and port.",ForeColor=UiTheme.Muted};hint.SetBounds(26,642,585,48);hint.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom;Controls.Add(hint);
            var close=new ModernButton {Text="Done",DialogResult=DialogResult.OK,Primary=true};close.SetBounds(646,657,130,36);close.Anchor=AnchorStyles.Right|AnchorStyles.Bottom;Controls.Add(close);AcceptButton=close;CancelButton=close;
            UiTheme.Apply(this);
        }
        static void AddDetail(Control parent,string caption,string value,int top,int height)
        {
            var label=new Label {Text=caption,ForeColor=UiTheme.Muted};label.SetBounds(16,top+3,124,24);parent.Controls.Add(label);
            var field=new TextBox {Text=value??"",ReadOnly=true,Multiline=height>30,BorderStyle=BorderStyle.None,TabStop=true,AccessibleName=caption};field.SetBounds(146,top,586,height);field.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;parent.Controls.Add(field);
        }
        public static string LocalDate(string value)
        {
            DateTimeOffset date;
            return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out date)?date.ToLocalTime().ToString("MMM d, yyyy  h:mm tt",CultureInfo.CurrentCulture):value;
        }
        public static string FileSize(long bytes)
        {
            return bytes>=1024L*1024*1024?(bytes/(1024d*1024*1024)).ToString("0.0",CultureInfo.CurrentCulture)+" GB":bytes>=1024L*1024?(bytes/(1024d*1024)).ToString("0.0",CultureInfo.CurrentCulture)+" MB":bytes>=1024? (bytes/1024d).ToString("0.0",CultureInfo.CurrentCulture)+" KB":bytes+" bytes";
        }
    }
}
