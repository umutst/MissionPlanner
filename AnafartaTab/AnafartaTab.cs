using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Linq;
using System.Windows.Forms;
using System.Net;
using System.Net.Http.Headers;
using MissionPlanner.Utilities;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;

public interface IActivate { void Activate(); }
public interface IDeactivate { void Deactivate(); }

namespace MissionPlanner.GCSViews
{
    public partial class AnafartaTab : MyUserControl, IActivate, IDeactivate
    {
        private SplitContainer _leftRightSplit;
        private SplitContainer _middleRightSplit;

        // Yeni alanlar: buton ve orta panel controllleri
        private Button _toggleButton;
        private bool _isLeftPanelExpanded = false;
        private FlightData flightData;
        private FlightPlanner flightPlanner;

        // --- Sunucu / UI alanları (sınıf alanı olarak eklendi) ---
        internal TextBox txtServerIp;
        internal Button btnServerConnect;
        internal Button btnToggleFetch;
        internal ListView lvEnemies;
        internal ColumnHeader chId;
        internal ColumnHeader chDistance;
        internal ColumnHeader chLat;
        internal ColumnHeader chLon;
        internal ColumnHeader chAlt;

        // Yeni: Takım ID alanı (UI tarafı burada)
        internal Label lblTeamNo;
        internal TextBox txtTeamNo;

        // Yeni: Login UI
        private TextBox txtUsername;
        private TextBox txtPassword;
        private Button btnLogin;

        // Log alanı (functions partial'dan erişilecek)
        // Eskiden tek `lstLogs` varken şimdi dikey iki bölme: lstLogs (sunucu A) ve lstServerLogsB (sunucu B)
        internal ListBox lstLogs;               // eski kullanım için bırakıldı -> varsayılan ilk sunucu kutusu
        internal ListBox lstServerLogsB;        // ikinci sunucu için kutu
        private Label lblServerHeaderA;
        private Label lblServerHeaderB;
        private readonly Dictionary<string, ListBox> _serverLogMap = new Dictionary<string, ListBox>(StringComparer.OrdinalIgnoreCase);
        // --------------------------------------------------------

        // Removed AnafartaTabFunctions dependency — use local state instead
        private int _teamNumber = 20;
        private bool _isFetching = false;

        // Networking & telemetry
        private HttpClient _httpClient;
        private HttpClientHandler _httpHandler;
        private CookieContainer _cookieContainer;
        private string _authToken; // eğer sunucu token dönerse saklamak için
        private Uri _serverBase;
        private System.Threading.Timer _telemetryTimer;
        private volatile bool _sendingTelemetry = false;

        // Map & markers
        private GMapControl _mapControl;
        private GMapOverlay _enemyOverlay;
        private readonly Dictionary<int, GMarkerGoogle> _enemyMarkers = new Dictionary<int, GMarkerGoogle>();

        // Yeni: çizgi overlay / rota
        private GMapOverlay _lineOverlay;
        private GMapRoute _nearestRoute;

        public AnafartaTab()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                // Eğer InitializeComponent'te bir hata olursa, kontrolün çökmesini engelle
                // ve hatayı ekranda göster.
                this.Controls.Clear();
                this.Controls.Add(new Label
                {
                    Text = "AnafartaTab yüklenirken bir hata oluştu:\n" + ex.ToString(),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.Red
                });
            }
        }

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;

            // Divider rengi (ince çubuk) ve kalınlık
            var dividerColor = Color.Red;

            _leftRightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = SystemColors.Control,
                IsSplitterFixed = true,
                FixedPanel = FixedPanel.None
            };

            _middleRightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = SystemColors.Control,
                IsSplitterFixed = true,
                FixedPanel = FixedPanel.Panel2 // Sağ panel sabit
            };

            // Görsel splitter çizimi korunuyor
            _leftRightSplit.Paint += (s, e) => DrawSplitter(e.Graphics, _leftRightSplit, dividerColor);
            _middleRightSplit.Paint += (s, e) => DrawSplitter(e.Graphics, _middleRightSplit, dividerColor);

            // Oranları hem Load hem Resize ile uygula.
            this.Load += (s, e) => UpdateSplitterProportions();
            this.Resize += (s, e) => UpdateSplitterProportions();

            // --- Bölüm 1: Sol Panel (FlightData) ---
            var leftPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            ThemeManager.ApplyThemeTo(leftPanel);
            try
            {
                flightData = new FlightData
                {
                    Dock = DockStyle.Fill,
                    Visible = true,
                    BackColor = Color.Transparent
                };
                typeof(FlightData).GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, flightData);
                leftPanel.Controls.Add(flightData);
                ThemeManager.ApplyThemeTo(flightData);
            }
            catch (Exception ex)
            {
                leftPanel.Controls.Add(new Label
                {
                    Text = "Bölüm 1 (FlightData yüklenemedi): " + ex.Message,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    AutoSize = false,
                    ForeColor = ThemeManager.TextColor
                });
            }

            // --- Bölüm 2: Orta Panel (FlightPlanner) ---
            var middlePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            flightPlanner = new FlightPlanner
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            try { FlightPlanner.instance = flightPlanner; } catch { }
            ThemeManager.ApplyThemeTo(flightPlanner);
            middlePanel.Controls.Add(flightPlanner);

            // --- Bölüm 3: Sağ Panel (Sinyal Durumu, Server, Log vb.) ---
            var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            try
            {
                // Ana iskelet (5 Satır)
                var mainLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Transparent,
                    ColumnCount = 1,
                    RowCount = 5,
                };
                
                // Satır yüksekliklerini ayarlıyoruz. 
                // ORTA ALAN esnek bırakıldı ki ekran değiştikçe burada scroll/resize düzgün olsun.
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // Toggle Butonlar (En üst)
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));  // Aksiyon Butonları
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // ORTA ALAN (Server + Hedef Listesi) - Esnek
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));   // Telemetri Butonları
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));  // Log Alanı

                // -----------------------------------------------------------------------
                // 1) Üst Toggle Buton Çubuğu
                // -----------------------------------------------------------------------
                var topBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2),
                    BackColor = Color.Transparent,
                    WrapContents = false
                };

                Action<Button> ApplyStandardButtonStyle = (b) =>
                {
                    b.BackColor = Color.White;
                    b.ForeColor = Color.Black;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 0;
                    b.Cursor = Cursors.Hand;
                    b.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
                };

                Func<string, EventHandler, Control> makeToggle = (text, click) =>
                {
                    var wrapper = new FlowLayoutPanel
                    {
                        AutoSize = true,
                        FlowDirection = FlowDirection.LeftToRight,
                        Margin = new Padding(2),
                        WrapContents = false
                    };
                    var b = new Button { Text = text, Size = new Size(50, 24), Margin = new Padding(0) };
                    ApplyStandardButtonStyle(b);
                    var indicator = new Label
                    {
                        Text = "OFF", Size = new Size(35, 24), Margin = new Padding(0),
                        TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Red, ForeColor = Color.White,
                        Font = new Font("Segoe UI", 7F, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle
                    };
                    
                    Action toggle = () => {
                        bool isOn = indicator.BackColor == Color.Green;
                        indicator.BackColor = isOn ? Color.Red : Color.Green;
                        indicator.Text = isOn ? "OFF" : "ON";
                    };
                    b.Click += (s, e) => { toggle(); click?.Invoke(s, e); };
                    indicator.Click += (s, e) => { toggle(); click?.Invoke(s, e); };
                    
                    wrapper.Controls.Add(b);
                    wrapper.Controls.Add(indicator);
                    return wrapper;
                };

                topBar.Controls.Add(makeToggle("QR", null));
                topBar.Controls.Add(makeToggle("HSS", null));
                topBar.Controls.Add(makeToggle("Kilit", null));
                ThemeManager.ApplyThemeTo(topBar);

                // -----------------------------------------------------------------------
                // 2) Aksiyon Butonları (3 Sütun)
                // -----------------------------------------------------------------------
                var actionsLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 3,
                    RowCount = 1,
                    BackColor = Color.Transparent,
                    Padding = new Padding(2)
                };
                actionsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
                actionsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
                actionsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

                Func<string, Button> makeAction = (txt) =>
                {
                    var btn = new Button { Text = txt, Dock = DockStyle.Top, Height = 30, Margin = new Padding(3) };
                    ApplyStandardButtonStyle(btn);
                    return btn;
                };
                
                Action<int, string[]> fillCol = (colIndex, texts) => {
                    var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true };
                    foreach(var t in texts) p.Controls.Add(makeAction(t));
                    actionsLayout.Controls.Add(p, colIndex, 0);
                };

                fillCol(0, new[] { "QR Det.", "Dalış" });
                fillCol(1, new[] { "Alan Yerleş", "Alan Sıfırla" });
                fillCol(2, new[] { "Hedefler", "Güncel Hedef" });
                ThemeManager.ApplyThemeTo(actionsLayout);

                // -----------------------------------------------------------------------
                // 3) ORTA PANEL: Server Girişi + Düşman Listesi + Hedef Seçimi
                // -----------------------------------------------------------------------
                var middleSectionPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(4) };

                var targetSplit = new SplitContainer 
                { 
                    Dock = DockStyle.Fill, 
                    Orientation = Orientation.Horizontal,
                    SplitterWidth = 4,
                    BackColor = SystemColors.ControlDark 
                };

                // --- ÜST KISIM: SERVER GİRİŞİ (Auto-Wrap özellikli) ---
                var serverHostPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
                
                var serverControlsFlow = new FlowLayoutPanel 
                { 
                    Dock = DockStyle.Top, 
                    AutoSize = true, 
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    Padding = new Padding(0,0,0,5)
                };

                // Küçültülmüş boyutlar, artık sabit Left/Top yok
                txtServerIp = new TextBox { Text = "http://127.0.0.1:5000", Width = 140, Height = 22 };
                btnServerConnect = new Button { Text = "Bağlan", Width = 60, Height = 24, BackColor = Color.White };
                btnToggleFetch = new Button { Text = "Fetch", Width = 60, Height = 24, BackColor = Color.White };
                
                txtUsername = new TextBox { Text = "kadi", Width = 80 };
                txtPassword = new TextBox { Text = "pass", Width = 80, UseSystemPasswordChar = true };
                btnLogin = new Button { Text = "Giriş", Width = 50, Height = 24, BackColor = Color.White };
                
                lblTeamNo = new Label { Text = "Takım:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0,6,0,0), ForeColor = ThemeManager.TextColor };
                txtTeamNo = new TextBox { Text = "20", Width = 40 };

                // Eventler korunuyor
                btnServerConnect.Click += BtnServerConnect_Click;
                btnToggleFetch.Click += BtnToggleFetch_Click;
                btnLogin.Click += BtnLogin_Click;
                txtTeamNo.TextChanged += TxtTeamNo_TextChanged;

                Control[] srvCtrls = { txtServerIp, btnServerConnect, btnToggleFetch, txtUsername, txtPassword, btnLogin, lblTeamNo, txtTeamNo };
                foreach(var c in srvCtrls) {
                    c.Margin = new Padding(2);
                    serverControlsFlow.Controls.Add(c);
                }

                // Düşman Listesi
                lvEnemies = new ListView
                {
                    View = View.Details,
                    Dock = DockStyle.Fill,
                    FullRowSelect = true,
                    BackColor = Color.White,
                    GridLines = true
                };
                lvEnemies.Columns.Add("ID", 40);
                lvEnemies.Columns.Add("Dist", 50);
                lvEnemies.Columns.Add("Lat", 70);
                lvEnemies.Columns.Add("Lon", 70);
                lvEnemies.Columns.Add("Alt", 50);

                // Z-order: önce Fill olan, sonra Top flow
                serverHostPanel.Controls.Add(lvEnemies);
                serverHostPanel.Controls.Add(serverControlsFlow);

                targetSplit.Panel1.Controls.Add(serverHostPanel);

                // --- ALT KISIM: HEDEF SEÇİMİ ---
                var selectionPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
                selectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                selectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                selectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                selectionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

                var lstIds = new ListBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(50,50,50), ForeColor = Color.White };
                for (int i = 1; i <= 5; i++) lstIds.Items.Add(i.ToString());
                
                var lstViewAngle = new ListBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(60,60,60), ForeColor = Color.White };
                lstViewAngle.Items.Add("Açı Yok");

                var btnSend = new Button { Text = "Gönder", Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(1) };
                var btnAuto = new Button { Text = "Oto Kilit", Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(1) };

                selectionPanel.Controls.Add(lstIds, 0, 0);
                selectionPanel.Controls.Add(lstViewAngle, 1, 0);
                selectionPanel.Controls.Add(btnSend, 0, 1);
                selectionPanel.Controls.Add(btnAuto, 1, 1);

                var headerLbl = new Label { Text = "Hedef Seçimi", Dock = DockStyle.Top, Height=20, BackColor=Color.Gray, ForeColor=Color.White, TextAlign=ContentAlignment.MiddleLeft };
                
                targetSplit.Panel2.Controls.Add(selectionPanel);
                targetSplit.Panel2.Controls.Add(headerLbl);

                middleSectionPanel.Controls.Add(targetSplit);
                ThemeManager.ApplyThemeTo(middleSectionPanel);

                // -----------------------------------------------------------------------
                // 4) Telemetri Butonları
                // -----------------------------------------------------------------------
                var telemetryBar = new FlowLayoutPanel 
                { 
                    Dock = DockStyle.Fill, 
                    FlowDirection = FlowDirection.LeftToRight, 
                    Padding = new Padding(2), 
                    BackColor = Color.Transparent 
                };
                Func<string, Button> makeTele = (txt) =>
                {
                    var b = new Button { Text = txt, Size = new Size(80, 30), Margin = new Padding(3), BackColor = Color.White };
                    b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize=0;
                    return b;
                };
                telemetryBar.Controls.Add(makeTele("3G"));
                telemetryBar.Controls.Add(makeTele("Ubi"));
                telemetryBar.Controls.Add(makeTele("Telem"));
                ThemeManager.ApplyThemeTo(telemetryBar);

                // -----------------------------------------------------------------------
                // 5) Log Alanı
                // -----------------------------------------------------------------------
                var logArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(2) };
                var logHeader = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(60, 60, 60) };
                var lblLogs = new Label { Text = "Logs", Dock = DockStyle.Left, Width = 60, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.White };
                var btnCollapse = new Button { Text = "Gizle", Dock = DockStyle.Right, Width = 60, BackColor = Color.LightGray, FlatStyle = FlatStyle.Flat };
                
                btnCollapse.Click += (s, e) => {
                    logArea.Visible = !logArea.Visible;
                };

                logHeader.Controls.Add(btnCollapse);
                logHeader.Controls.Add(lblLogs);

                var logSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4, BackColor = Color.Gray };
                
                lblServerHeaderA = new Label { Text = "Server A", Dock = DockStyle.Top, Height=18, ForeColor=Color.White, BackColor=Color.Black };
                lstLogs = new ListBox { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.Lime, BorderStyle = BorderStyle.None };
                logSplit.Panel1.Controls.Add(lstLogs);
                logSplit.Panel1.Controls.Add(lblServerHeaderA);

                lblServerHeaderB = new Label { Text = "Server B", Dock = DockStyle.Top, Height=18, ForeColor=Color.White, BackColor=Color.Black };
                lstServerLogsB = new ListBox { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.Lime, BorderStyle = BorderStyle.None };
                logSplit.Panel2.Controls.Add(lstServerLogsB);
                logSplit.Panel2.Controls.Add(lblServerHeaderB);

                logArea.Controls.Add(logSplit);
                logArea.Controls.Add(logHeader);

                // -----------------------------------------------------------------------
                // ANA LAYOUT'A EKLEME
                // -----------------------------------------------------------------------
                mainLayout.Controls.Add(topBar, 0, 0);
                mainLayout.Controls.Add(actionsLayout, 0, 1);
                mainLayout.Controls.Add(middleSectionPanel, 0, 2);
                mainLayout.Controls.Add(telemetryBar, 0, 3);
                mainLayout.Controls.Add(logArea, 0, 4);

                rightPanel.Controls.Add(mainLayout);
            }
            catch (Exception ex)
            {
                 // Hata yakalama
                 rightPanel.Controls.Add(new Label { Text = "UI Hatası: " + ex.Message, Dock = DockStyle.Fill, ForeColor = Color.Red });
            }

            // Panelleri ana splitter'lara ata
            _leftRightSplit.Panel1.Controls.Add(leftPanel);
            _leftRightSplit.Panel2.Controls.Add(_middleRightSplit);

            _middleRightSplit.Panel1.Controls.Add(middlePanel);
            _middleRightSplit.Panel2.Controls.Add(rightPanel);

            this.Controls.Add(_leftRightSplit);

            // --- Geçiş Butonu ---
            _toggleButton = new Button
            {
                Text = ">",
                Size = new Size(24, 40),
                FlatStyle = FlatStyle.Standard,
                BackColor = Color.Gray,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _toggleButton.Click += ToggleButton_Click;
            this.Controls.Add(_toggleButton); // Butonu splitter yerine AnafartaTab'ın kendisine ekle

            // Butonun konumunu splitter hareket ettiğinde güncelle
            _leftRightSplit.SplitterMoved += (s, e) => UpdateToggleButtonPosition();

            // Tüm Button kontrollerinin arkaplanını zorla beyaz yap (indicator label'lar etkilenmez)
            EnsureAllButtonsWhite(this);

            ThemeManager.ApplyThemeTo(this);

            // Map control lookup (try to find map inside FlightPlanner for later marker updates)
            this.Load += (s, e) =>
            {
                TryFindMapControl();
            };
        }

        private void ToggleButton_Click(object sender, EventArgs e)
        {
            _isLeftPanelExpanded = !_isLeftPanelExpanded;
            UpdateSplitterProportions();
        }

        private void UpdateToggleButtonPosition()
        {
            if (_toggleButton == null) return;
            // Butonu splitter'ın ortasına dikey olarak yerleştir
            _toggleButton.Left = _leftRightSplit.SplitterRectangle.Left + (_leftRightSplit.SplitterRectangle.Width - _toggleButton.Width) / 2;
            _toggleButton.Top = _leftRightSplit.SplitterRectangle.Top + (_leftRightSplit.Height - _toggleButton.Height) / 2;
            _toggleButton.BringToFront();
        }

        // takım ID değişimlerini burada ele alıyoruz (UI tarafı)
        private void TxtTeamNo_TextChanged(object sender, EventArgs e)
        {
            try
			{
				var tb = sender as TextBox;
				if (tb == null) return;
				var txt = (tb.Text ?? "").Trim();
				if (string.IsNullOrEmpty(txt)) return;

				if (int.TryParse(txt, out int val) && val >= 0)
				{
					// artık yerel state güncelleniyor
					_teamNumber = val;
					AppendLogInternal($"Takım ID set: {val}");
				}
				else
				{
					// Geçersizse UI'ya yerel değeri geri yaz
					try
					{
						var cur = _teamNumber;
						if (txtTeamNo.Text != cur.ToString()) txtTeamNo.Text = cur.ToString();
					}
					catch { }
				}
			}
			catch (Exception ex)
			{
				// minimal logging
				try { AppendLogInternal("TxtTeamNo_TextChanged hata: " + ex.Message); } catch { }
			}
        }

        // küçük yardımcı: AnafartaTab içindeki genel log listesine ekleme (eski davranış korunur)
        private void AppendLogInternal(string message)
        {
            try
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                var item = $"[{ts}] {message}";
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (this.lstLogs == null) return;
                            this.lstLogs.Items.Insert(0, item);
                            while (this.lstLogs.Items.Count > 200) this.lstLogs.Items.RemoveAt(this.lstLogs.Items.Count - 1);
                        }
                        catch { }
                    }));
                }
            }
            catch { }
        }

        // Yeni: belirli bir sunucuya ait log ekleme / iki bölmeli log paneline atama mantığı
        private void AppendServerStatus(string serverKey, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(serverKey)) serverKey = "unknown";
                var ts = DateTime.Now.ToString("HH:mm:ss");
                var item = $"[{ts}] {message}";

                if (!this.IsHandleCreated) return;

                this.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        ListBox target;
                        if (!_serverLogMap.TryGetValue(serverKey, out target))
                        {
                            // assign to first free slot (A then B), otherwise reuse A
                            if (_serverLogMap.Count == 0)
                            {
                                target = lstLogs; // A
                                _serverLogMap[serverKey] = target;
                                UpdateServerHeader(0, serverKey);
                            }
                            else if (_serverLogMap.Count == 1)
                            {
                                target = lstServerLogsB; // B
                                _serverLogMap[serverKey] = target;
                                UpdateServerHeader(1, serverKey);
                            }
                            else
                            {
                                // more than 2 servers: fallback to A with prefix
                                target = lstLogs;
                                _serverLogMap[serverKey] = target;
                            }
                        }

                        if (target != null)
                        {
                            target.Items.Insert(0, item);
                            while (target.Items.Count > 500) target.Items.RemoveAt(target.Items.Count - 1);
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void UpdateServerHeader(int index, string serverKey)
        {
            try
            {
                if (index == 0)
                {
                    if (lblServerHeaderA != null) lblServerHeaderA.Text = serverKey;
                }
                else if (index == 1)
                {
                    if (lblServerHeaderB != null) lblServerHeaderB.Text = serverKey;
                }
            }
            catch { }
        }

        // Tüm Button kontrollerinin arkaplanını beyaza zorlar; font ve renk de sabitlenir.
        private void EnsureAllButtonsWhite(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                try
                {
                    if (c is Button btn && c != _toggleButton) // Toggle butonu hariç
                    {
                        btn.BackColor = Color.White;
                        btn.ForeColor = Color.Black;
                        btn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderSize = 0;
                    }
                    else if (c.HasChildren) // Diğer container kontrolleri için recursive çağrı
                    {
                        EnsureAllButtonsWhite(c);
                    }
                }
                catch
                {
                    // best-effort, yoksay
                }
            }
        }

        // Server bağlantı butonu click -> artık yerel davranış (AnafartaTabFunctions kaldırıldı)
        private async void BtnServerConnect_Click(object sender, EventArgs e)
        {
            try
            {
                var txt = (txtServerIp?.Text ?? "").Trim();
                AppendLogInternal("Server connect clicked: " + txt);

                if (string.IsNullOrEmpty(txt))
                {
                    AppendLogInternal("Sunucu adresi boş");
                    return;
                }

                if (!txt.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !txt.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    txt = "http://" + txt;
                }

                if (!Uri.TryCreate(txt, UriKind.Absolute, out Uri baseUri))
                {
                    AppendLogInternal("Geçersiz URL");
                    return;
                }

                _serverBase = baseUri;

                // Dispose previous client/handler and create a cookie-enabled HttpClient
                try { _httpClient?.Dispose(); } catch { }
                try { _httpHandler = null; _cookieContainer = null; } catch { }

                _cookieContainer = new CookieContainer();
                _httpHandler = new HttpClientHandler { CookieContainer = _cookieContainer, UseCookies = true, AllowAutoRedirect = true };
                _httpClient = new HttpClient(_httpHandler) { BaseAddress = _serverBase, Timeout = TimeSpan.FromSeconds(5) };

                var serverKey = _serverBase?.Host ?? txt;

                // Try GET /api/sunucusaati to verify server reachable
                AppendLogInternal("Sunucuya sunucusaati isteği gönderiliyor...");
                AppendServerStatus(serverKey, "Sunucuya sunucusaati isteği gönderiliyor...");

                try
                {
                    var resp = await _httpClient.GetAsync("/api/sunucusaati");
                    if (resp.IsSuccessStatusCode)
                    {
                        AppendLogInternal("Sunucuya bağlanıldı (sunucusaati).");
                        AppendServerStatus(serverKey, "Sunucuya bağlanıldı (sunucusaati).");
                        // don't parse deeply here
                    }
                    else
                    {
                        var m = $"Sunucu yanıtı: {(int)resp.StatusCode} {resp.ReasonPhrase}";
                        AppendLogInternal(m);
                        AppendServerStatus(serverKey, m);
                    }
                }
                catch (Exception ex)
                {
                    AppendLogInternal("Sunucuya erişilemedi: " + ex.Message);
                    AppendServerStatus(serverKey, "Sunucuya erişilemedi: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AppendLogInternal("BtnServerConnect_Click hata: " + ex.Message);
            }
        }

        // Login butonu: /api/giris POST eder
        private async void BtnLogin_Click(object sender, EventArgs e)
        {
            try
            {
                if (_httpClient == null || _serverBase == null)
                {
                    AppendLogInternal("Önce sunucuya bağlanın (Bağlan düğmesi).");
                    return;
                }

                var kadi = (txtUsername?.Text ?? "").Trim();
                var sifre = (txtPassword?.Text ?? "").Trim();

                if (string.IsNullOrEmpty(kadi) || string.IsNullOrEmpty(sifre))
                {
                    AppendLogInternal("Kullanıcı adı veya şifre boş.");
                    return;
                }

                var payload = new { kadi = kadi, sifre = sifre };
                string json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var serverKey = _serverBase?.Host ?? "server";
                AppendLogInternal("Giriş isteği gönderiliyor...");
                AppendServerStatus(serverKey, "Giriş isteği gönderiliyor...");

                HttpResponseMessage resp = null;
                try
                {
                    resp = await _httpClient.PostAsync("/api/giris", content);
                }
                catch (Exception ex)
                {
                    AppendLogInternal("Giriş isteği hata: " + ex.Message);
                    AppendServerStatus(serverKey, "Giriş isteği hata: " + ex.Message);
                    return;
                }

                if (resp == null)
                {
                    AppendLogInternal("Sunucu yanıtı alınamadı (null).");
                    AppendServerStatus(serverKey, "Sunucu yanıtı alınamadı (null).");
                    return;
                }

                if (resp.IsSuccessStatusCode)
                {
                    AppendLogInternal("Giriş başarılı.");
                    AppendServerStatus(serverKey, "Giriş başarılı.");

                    // Eğer response body JSON içinde token varsa al (örneğin { token: "..." })
                    try
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith("{"))
                        {
                            dynamic dobj = JsonConvert.DeserializeObject(body);
                            if (dobj != null)
                            {
                                try
                                {
                                    var token = (string)dobj.token;
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        _authToken = token;
                                        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                                        AppendLogInternal("Token saklandı (Authorization header ayarlandı).");
                                    }
                                }
                                catch { /* token yoksa yoksay */ }
                            }
                        }
                    }
                    catch { /* parse hatası yoksay */ }

                    // Cookie varsa CookieContainer içine otomatik gelmiş olacaktır.
                }
                else if ((int)resp.StatusCode == 401)
                {
                    AppendLogInternal("Giriş başarısız: 401 Kimliksiz erişim.");
                    AppendServerStatus(serverKey, "Giriş başarısız: 401.");
                }
                else
                {
                    AppendLogInternal($"Giriş başarısız: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    AppendServerStatus(serverKey, $"Giriş başarısız: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                AppendLogInternal("BtnLogin_Click hata: " + ex.Message);
            }
        }

        // Fetch toggle click -> artık yerel implementasyon
        private void BtnToggleFetch_Click(object sender, EventArgs e)
        {
            try
            {
                _isFetching = !_isFetching;
                if (_isFetching)
                {
                    if (_httpClient == null || _serverBase == null)
                    {
                        AppendLogInternal("Önce sunucuya bağlanın");
                        _isFetching = false;
                        if (btnToggleFetch != null) btnToggleFetch.Text = "Fetch: Kapalı";
                        return;
                    }

                    // Oturum kontrolü: fetch başlamadan önce giriş yapılmış mı?
                    if (!IsLoggedIn())
                    {
                        AppendLogInternal("Fetch başlatılamadı: Sunucuya giriş yapılmamış. Lütfen önce Giriş yapın.");
                        AppendServerStatus(_serverBase?.Host ?? "server", "Fetch başlatılamadı: Giriş gerekli.");
                        _isFetching = false;
                        if (btnToggleFetch != null) btnToggleFetch.Text = "Fetch: Kapalı";
                        return;
                    }

                    if (btnToggleFetch != null) btnToggleFetch.Text = "Fetch: Açık";
                    AppendLogInternal("Fetch başlatıldı");
                    AppendServerStatus(_serverBase?.Host ?? "server", "Fetch başlatıldı");
                    StartTelemetryTimer();
                }
                else
                {
                    if (btnToggleFetch != null) btnToggleFetch.Text = "Fetch: Kapalı";
                    AppendLogInternal("Fetch durduruldu");
                    AppendServerStatus(_serverBase?.Host ?? "server", "Fetch durduruldu");
                    StopTelemetryTimer();
                }
            }
            catch { }
        }

        private void StartTelemetryTimer()
        {
            try
            {
                StopTelemetryTimer();
                // Timer runs on threadpool; callback will manage async send
                _telemetryTimer = new System.Threading.Timer(TelemetryTimerCallback, null, 0, 1000);
            }
            catch (Exception ex)
            {
                AppendLogInternal("StartTelemetryTimer hata: " + ex.Message);
            }
        }

        private void StopTelemetryTimer()
        {
            try
            {
                _telemetryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _telemetryTimer?.Dispose();
                _telemetryTimer = null;
            }
            catch { }
        }

        private void TelemetryTimerCallback(object state)
        {
            if (_sendingTelemetry) return;
            _sendingTelemetry = true;
            Task.Run(async () =>
            {
                try
                {
                    await SendTelemetryOnceAsync();
                }
                catch (Exception ex)
                {
                    AppendLogInternal("Telemetry send hatası: " + ex.Message);
                    AppendServerStatus(_serverBase?.Host ?? "server", "Telemetry send hatası: " + ex.Message);
                }
                finally
                {
                    _sendingTelemetry = false;
                }
            });
        }

        // Gerekli JSON DTO'lar
        private class GpsTimeDto
        {
            public int gun { get; set; }
            public int saat { get; set; }
            public int dakika { get; set; }
            public int saniye { get; set; }
            public int milisaniye { get; set; }
        }

        private class TelemetryPayload
        {
            public int takim_numarasi { get; set; }
            public double iha_enlem { get; set; }
            public double iha_boylam { get; set; }
            public double iha_irtifa { get; set; }
            public double iha_dikilme { get; set; }
            public double iha_yonelme { get; set; }
            public double iha_yatis { get; set; }
            public double iha_hiz { get; set; }
            public int iha_batarya { get; set; }
            public int iha_otonom { get; set; }
            public int iha_kilitlenme { get; set; }
            public int hedef_merkez_X { get; set; }
            public int hedef_merkez_Y { get; set; }
            public int hedef_genislik { get; set; }
            public int hedef_yukseklik { get; set; }
            public GpsTimeDto gps_saati { get; set; }
        }

        private class EnemyDto
        {
            public int takim_numarasi { get; set; }
            public double iha_enlem { get; set; }
            public double iha_boylam { get; set; }
            public double iha_irtifa { get; set; }
            public double iha_dikilme { get; set; }
            public double iha_yonelme { get; set; }
            public double iha_yatis { get; set; }
            [JsonProperty("iha_hizi")]
            public double iha_hizi { get; set; }
            public int zaman_farki { get; set; }
        }

        private class TelemetryResponse
        {
            public GpsTimeDto sunucusaati { get; set; }
            public List<EnemyDto> konumBilgileri { get; set; }
        }

        // main send function
        private async Task SendTelemetryOnceAsync()
        {
            if (_httpClient == null || _serverBase == null) return;

            var serverKey = _serverBase?.Host ?? "server";

            try
            {
                var payload = BuildTelemetryPayload();

                string json = JsonConvert.SerializeObject(payload);

                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                HttpResponseMessage resp = null;
                try
                {
                    resp = await _httpClient.PostAsync("/api/telemetri_gonder", content);
                }
                catch (Exception ex)
                {
                    AppendLogInternal("HTTP POST hata: " + ex.Message);
                    AppendServerStatus(serverKey, "HTTP POST hata: " + ex.Message);
                    return;
                }

                if (resp == null)
                {
                    AppendLogInternal("HTTP yanıtı null");
                    AppendServerStatus(serverKey, "HTTP yanıtı null");
                    return;
                }

                var code = (int)resp.StatusCode;
                if (code == 200)
                {
                    string respStr = await resp.Content.ReadAsStringAsync();
                    TelemetryResponse tr = null;
                    try
                    {
                        tr = JsonConvert.DeserializeObject<TelemetryResponse>(respStr);
                    }
                    catch (Exception ex)
                    {
                        AppendLogInternal("Sunucu cevabı parse edilemedi: " + ex.Message);
                        AppendServerStatus(serverKey, "Sunucu cevabı parse edilemedi: " + ex.Message);
                    }

                    if (tr != null)
                    {
                        UpdateEnemiesOnUI(tr.konumBilgileri);
                        AppendServerStatus(serverKey, "Konum bilgileri alındı: " + (tr.konumBilgileri?.Count ?? 0) + " hedef.");
                    }
                    AppendLogInternal("Telemetri başarı ile gönderildi (200).");
                    AppendServerStatus(serverKey, "Telemetri başarı ile gönderildi (200).");
                }
                else if (code == 204)
                {
                    AppendLogInternal("Sunucu: 204 - Gönderilen paketin formatı yanlış.");
                    AppendServerStatus(serverKey, "Sunucu: 204 - Gönderilen paketin formatı yanlış.");
                }
                else if (code == 400)
                {
                    AppendLogInternal("Sunucu: 400 - İstek hatalı veya geçersiz.");
                    AppendServerStatus(serverKey, "Sunucu: 400 - İstek hatalı veya geçersiz.");
                }
                else if (code == 401)
                {
                    AppendLogInternal("Sunucu: 401 - Kimliksiz erişim.");
                    AppendServerStatus(serverKey, "Sunucu: 401 - Kimliksiz erişim.");

                    // Oturum yoksa otomatik olarak fetch'i durdur ve kullanıcıyı uyar
                    _isFetching = false;
                    try { StopTelemetryTimer(); } catch { }
                    if (btnToggleFetch != null) this.BeginInvoke((Action)(() => btnToggleFetch.Text = "Fetch: Kapalı"));
                    try { this.BeginInvoke((Action)(() => MessageBox.Show("Sunucu 401 döndü. Lütfen yeniden giriş yapın.", "Oturum Gerekli", MessageBoxButtons.OK, MessageBoxIcon.Warning))); } catch { }
                }
                else if (code == 403)
                {
                    AppendLogInternal("Sunucu: 403 - Yetkisiz erişim.");
                    AppendServerStatus(serverKey, "Sunucu: 403 - Yetkisiz erişim.");
                }
                else if (code == 404)
                {
                    AppendLogInternal("Sunucu: 404 - Geçersiz URL.");
                    AppendServerStatus(serverKey, "Sunucu: 404 - Geçersiz URL.");
                }
                else if (code == 500)
                {
                    AppendLogInternal("Sunucu: 500 - Sunucu içi hata.");
                    AppendServerStatus(serverKey, "Sunucu: 500 - Sunucu içi hata.");
                }
                else
                {
                    AppendLogInternal($"Sunucu durum kodu: {code} ({resp.ReasonPhrase})");
                    AppendServerStatus(serverKey, $"Sunucu durum kodu: {code} ({resp.ReasonPhrase})");
                }
            }
            catch (Exception ex)
            {
                AppendLogInternal("SendTelemetryOnceAsync hata: " + ex.Message);
                AppendServerStatus(serverKey, "SendTelemetryOnceAsync hata: " + ex.Message);
            }
        }

        // Build telemetry DTO from MissionPlanner state (best-effort; missing data => defaults)
        private TelemetryPayload BuildTelemetryPayload()
        {
            var p = new TelemetryPayload
            {
                takim_numarasi = _teamNumber,
                iha_enlem = 0,
                iha_boylam = 0,
                iha_irtifa = 0,
                iha_dikilme = 0,
                iha_yonelme = 0,
                iha_yatis = 0,
                iha_hiz = 0,
                iha_batarya = 0,
                iha_otonom = 0,
                iha_kilitlenme = 0,
                hedef_merkez_X = 0,
                hedef_merkez_Y = 0,
                hedef_genislik = 0,
                hedef_yukseklik = 0,
                gps_saati = new GpsTimeDto()
            };

            try
            {
                // Try to get MAV state quickly via MainV2
                var mav = MainV2.comPort?.MAV?.cs;
                if (mav != null)
                {
                    // Use reflection-safe getters
                    p.iha_enlem = SafeGetDouble(mav, "lat", p.iha_enlem);
                    p.iha_boylam = SafeGetDouble(mav, "lng", p.iha_boylam);
                    p.iha_irtifa = SafeGetDouble(mav, "alt", p.iha_irtifa);
                    p.iha_dikilme = SafeGetDouble(mav, "pitch", p.iha_dikilme);
                    // iha_yonelme = heading (groundcourse or yaw)
                    p.iha_yonelme = SafeGetDouble(mav, "groundcourse", SafeGetDouble(mav, "yaw", p.iha_yonelme));
                    p.iha_yatis = SafeGetDouble(mav, "roll", p.iha_yatis);
                    p.iha_hiz = SafeGetDouble(mav, "groundspeed", p.iha_hiz);

                    // battery percentage: try known names or reflect private field
                    int battery = 0;
                    try
                    {
                        var br = SafeGetInt(mav, "battery_remaining", -1);
                        if (br >= 0) battery = br;
                        else
                        {
                            // sometimes property named "battery" or "bat"
                            br = SafeGetInt(mav, "bat", -1);
                            if (br >= 0) battery = br;
                        }
                    }
                    catch { }
                    p.iha_batarya = Math.Max(0, Math.Min(100, battery));

                    // autonomy: if mode contains GUIDED or autopilot armed? best-effort
                    try
                    {
                        var armed = SafeGetBool(mav, "armed", false);
                        p.iha_otonom = armed ? 1 : 0;
                    }
                    catch { p.iha_otonom = 0; }

                    // gps time if available (gpstime)
                    try
                    {
                        var gpstimeObj = GetPropertyValue(mav, "gpstime");
                        if (gpstimeObj is DateTime dt)
                        {
                            p.gps_saati = ToGpsTimeDto(dt.ToUniversalTime());
                        }
                        else
                        {
                            p.gps_saati = ToGpsTimeDto(DateTime.UtcNow);
                        }
                    }
                    catch
                    {
                        p.gps_saati = ToGpsTimeDto(DateTime.UtcNow);
                    }
                }
                else
                {
                    // fallback gps time now
                    p.gps_saati = ToGpsTimeDto(DateTime.UtcNow);
                }
            }
            catch { p.gps_saati = ToGpsTimeDto(DateTime.UtcNow); }

            return p;
        }

        private static GpsTimeDto ToGpsTimeDto(DateTime dtUtc)
        {
            return new GpsTimeDto
            {
                gun = dtUtc.Day,
                saat = dtUtc.Hour,
                dakika = dtUtc.Minute,
                saniye = dtUtc.Second,
                milisaniye = dtUtc.Millisecond
            };
        }

        // Reflection helpers (best-effort)
        private static object GetPropertyValue(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null) return pi.GetValue(obj);
            var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null) return fi.GetValue(obj);
            // try camelCase / Pascal alternatives
            var alt = t.GetProperty(Char.ToUpperInvariant(name[0]) + name.Substring(1), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (alt != null) return alt.GetValue(obj);
            return null;
        }

        private static double SafeGetDouble(object obj, string name, double fallback)
        {
            try
            {
                var v = GetPropertyValue(obj, name);
                if (v == null) return fallback;
                if (v is double) return (double)v;
                if (v is float) return Convert.ToDouble((float)v);
                if (v is int) return Convert.ToDouble((int)v);
                if (v is long) return Convert.ToDouble((long)v);
                double parsed;
                if (double.TryParse(Convert.ToString(v), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            catch { }
            return fallback;
        }

        private static int SafeGetInt(object obj, string name, int fallback)
        {
            try
            {
                var v = GetPropertyValue(obj, name);
                if (v == null) return fallback;
                if (v is int) return (int)v;
                if (v is short) return (int)(short)v;
                if (v is byte) return (int)(byte)v;
                if (v is long) return (int)(long)v;
                int parsed;
                if (int.TryParse(Convert.ToString(v), out parsed)) return parsed;
            }
            catch { }
            return fallback;
        }

        private static bool SafeGetBool(object obj, string name, bool fallback)
        {
            try
            {
                var v = GetPropertyValue(obj, name);
                if (v == null) return fallback;
                if (v is bool) return (bool)v;
                bool parsed;
                if (bool.TryParse(Convert.ToString(v), out parsed)) return parsed;
                int ival;
                if (int.TryParse(Convert.ToString(v), out ival)) return ival != 0;
            }
            catch { }
            return fallback;
        }

        // Update enemies both listview and map
        private void UpdateEnemiesOnUI(List<EnemyDto> enemies)
        {
            try
            {
                if (enemies == null) enemies = new List<EnemyDto>();

                // capture our current pos for distance calculation
                double myLat = 0, myLon = 0;
                try
                {
                    var mav = MainV2.comPort?.MAV?.cs;
                    if (mav != null)
                    {
                        myLat = SafeGetDouble(mav, "lat", 0);
                        myLon = SafeGetDouble(mav, "lng", 0);
                    }
                }
                catch { }

                // Update ListView on UI thread
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            lvEnemies.BeginUpdate();
                            lvEnemies.Items.Clear();
                            foreach (var e in enemies)
                            {
                                double dist = HaversineDistanceMeters(myLat, myLon, e.iha_enlem, e.iha_boylam);
                                var li = new ListViewItem(new[] {
                                    e.takim_numarasi.ToString(),
                                    Math.Round(dist).ToString(),
                                    e.iha_enlem.ToString("F6"),
                                    e.iha_boylam.ToString("F6"),
                                    e.iha_irtifa.ToString()
                                });
                                lvEnemies.Items.Add(li);
                            }
                            lvEnemies.EndUpdate();
                        }
                        catch { }
                    }));
                }

                // Update map markers
                if (_mapControl == null)
                {
                    TryFindMapControl();
                }

                if (_mapControl != null)
                {
                    // ensure overlay exists
                    if (_enemyOverlay == null)
                    {
                        _enemyOverlay = new GMapOverlay("enemies");
                        _mapControl.Overlays.Add(_enemyOverlay);
                    }

                    // Update markers on UI thread (GMap control requires UI thread)
                    if (_mapControl.InvokeRequired)
                    {
                        _mapControl.BeginInvoke((Action)(() => UpdateMarkersInternal(enemies)));
                    }
                    else
                    {
                        UpdateMarkersInternal(enemies);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLogInternal("UpdateEnemiesOnUI hata: " + ex.Message);
                AppendServerStatus(_serverBase?.Host ?? "server", "UpdateEnemiesOnUI hata: " + ex.Message);
            }
        }

        private void UpdateMarkersInternal(List<EnemyDto> enemies)
        {
            try
            {
                _enemyOverlay.Markers.Clear();
                _enemyMarkers.Clear();

                // current own position (try to get from MAV)
                double myLat = 0, myLon = 0;
                try
                {
                    var mav = MainV2.comPort?.MAV?.cs;
                    if (mav != null)
                    {
                        myLat = SafeGetDouble(mav, "lat", 0);
                        myLon = SafeGetDouble(mav, "lng", 0);
                    }
                }
                catch { }

                int? nearestId = null;
                double nearestDist = double.MaxValue;
                PointLatLng nearestPt = new PointLatLng(0, 0);

                foreach (var e in enemies)
                {
                    var p = new PointLatLng(e.iha_enlem, e.iha_boylam);
                    var marker = new GMarkerGoogle(p, GMarkerGoogleType.red_small)
                    {
                        ToolTipText = $"Takım: {e.takim_numarasi}\nAlt: {e.iha_irtifa}m\nHız: {e.iha_hizi} m/s",
                        Tag = e.takim_numarasi
                    };
                    _enemyOverlay.Markers.Add(marker);
                    _enemyMarkers[e.takim_numarasi] = marker;

                    try
                    {
                        double dist = HaversineDistanceMeters(myLat, myLon, e.iha_enlem, e.iha_boylam);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestId = e.takim_numarasi;
                            nearestPt = p;
                        }
                    }
                    catch { }
                }

                // Draw dashed white line from our position to nearest enemy (if available)
                try
                {
                    if (_lineOverlay == null)
                    {
                        _lineOverlay = new GMapOverlay("lines");
                        _mapControl.Overlays.Add(_lineOverlay);
                    }

                    // clear previous drawn lines
                    _lineOverlay.Routes.Clear();
                    _nearestRoute = null;

                    // ensure valid coordinates (non-zero)
                    if (nearestId.HasValue && Math.Abs(myLat) > 1e-9 && Math.Abs(myLon) > 1e-9)
                    {
                        var pts = new List<PointLatLng> { new PointLatLng(myLat, myLon), nearestPt };
                        var route = new GMapRoute(pts, "nearestRoute");
                        // dashed white pen
                        var pen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash };
                        route.Stroke = pen;
                        _lineOverlay.Routes.Add(route);
                        _nearestRoute = route;
                    }
                }
                catch { /* best-effort */ }

                // force refresh
                try { _mapControl.Refresh(); } catch { }
            }
            catch { }
        }

        private void UpdateMarkersInternal_Old(List<EnemyDto> enemies)
        {
            // kept for reference if needed
        }

        // Try to find map control inside FlightPlanner using reflection and name heuristics
        private void TryFindMapControl()
        {
            try
            {
                if (flightPlanner == null) return;

                // First try known field names
                string[] fieldCandidates = new[] { "MainMap", "panelMap", "gMapControl1", "mymap" };

                foreach (var fn in fieldCandidates)
                {
                    try
                    {
                        var fi = flightPlanner.GetType().GetField(fn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fi != null)
                        {
                            var val = fi.GetValue(flightPlanner) as Control;
                            if (val != null && (val.GetType().Name.IndexOf("GMap", StringComparison.OrdinalIgnoreCase) >= 0 || val.GetType().Name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                _mapControl = val as GMapControl;
                                if (_mapControl != null) return;
                            }
                        }
                    }
                    catch { }
                }

                // fallback: search child controls for GMapControl by type-name
                foreach (Control c in flightPlanner.Controls)
                {
                    try
                    {
                        if (c == null) continue;
                        var tn = c.GetType().Name.ToLowerInvariant();
                        if (tn.Contains("gmap") || tn.Contains("map"))
                        {
                            _mapControl = c as GMapControl;
                            if (_mapControl != null) return;
                        }
                    }
                    catch { }
                }

                // recursive search
                _mapControl = FindControlByTypeNameRecursive(flightPlanner, "gmapcontrol") as GMapControl;
            }
            catch { }
        }

        private Control FindControlByTypeNameRecursive(Control root, string typeNameLower)
        {
            if (root == null) return null;
            foreach (Control c in root.Controls)
            {
                try
                {
                    if (c.GetType().Name.ToLowerInvariant().Contains(typeNameLower))
                        return c;
                    var found = FindControlByTypeNameRecursive(c, typeNameLower);
                    if (found != null) return found;
                }
                catch { }
            }
            return null;
        }

        // Haversine distance in meters
        private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            try
            {
                const double R = 6371000; // meters
                double dLat = ToRad(lat2 - lat1);
                double dLon = ToRad(lon2 - lon1);
                double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                           Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                           Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
                double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
                return R * c;
            }
            catch { return double.MaxValue; }
        }

        private static double ToRad(double deg) { return deg * Math.PI / 180.0; }

        private void DrawSplitter(Graphics g, SplitContainer sc, Color color)
        {
            try
            {
                var rect = sc.SplitterRectangle;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    using (var b = new SolidBrush(color))
                    {
                        g.FillRectangle(b, rect);
                    }
                }
            }
            catch
            {
                // Paint sırasında hata olursa yoksay
            }
        }

        // Yeni: sol/orta/sağ oranlarını ayarlar
        private void UpdateSplitterProportions()
        {
            this.SuspendLayout(); // Titremeyi engeller
            try
            {
                if (this.ClientSize.Width <= 0) return;

                _toggleButton.Text = _isLeftPanelExpanded ? "<" : ">";

                // Sağ panelin hedef genişliği (Toplamın %25'i)
                int totalWidth = this.ClientSize.Width;
                int rightPanelTargetWidth = (int)(totalWidth * 0.25);

                // Minimum boyut koruması
                if (rightPanelTargetWidth < _middleRightSplit.Panel2MinSize)
                    rightPanelTargetWidth = _middleRightSplit.Panel2MinSize;

                if (_isLeftPanelExpanded)
                {
                    // --- GENİŞLETME MODU (SOL BÜYÜK, ORTA YOK, SAĞ KÜÇÜK) ---

                    // 1. Orta Paneli tamamen gizle (Bu işlem splitter çubuğunu da yok eder!)
                    // Böylece 'çift splitter' veya 'gap' sorunu oluşmaz.
                    _middleRightSplit.Panel1Collapsed = true;

                    // 2. Dış splitter'ı ayarla: Sol Panel + Sağ Panel kaldı sadece.
                    // SplitterDistance, sol panelin genişliğidir.
                    // Sol Panel = Toplam - Sağ Panel - Dış Splitter Kalınlığı
                    int targetLeftSize = _leftRightSplit.Width - rightPanelTargetWidth - _leftRightSplit.SplitterWidth;

                    if (targetLeftSize > _leftRightSplit.Panel1MinSize)
                        _leftRightSplit.SplitterDistance = targetLeftSize;
                }
                else
                {
                    // --- VARSAYILAN MOD (HEPSİ GÖRÜNÜR) ---

                    // 1. Orta Paneli geri getir
                    _middleRightSplit.Panel1Collapsed = false;
                    
                    // 2. Sol Paneli %243 yap
                    int leftPanelTargetWidth = (int)(totalWidth * 0.243);

                    // Dış splitter ayarı
                    if (leftPanelTargetWidth > _leftRightSplit.Panel1MinSize)
                        _leftRightSplit.SplitterDistance = leftPanelTargetWidth;

                    // 3. İç splitter ayarı (Orta ve Sağ arasındaki denge)
                    // İç splitter mesafesi = İç Genişlik - Sağ Panel - Splitter Kalınlığı
                    int innerTotalWidth = _middleRightSplit.Width;
                    int innerSplitterDist = innerTotalWidth - rightPanelTargetWidth - _middleRightSplit.SplitterWidth;

                    if (innerSplitterDist > _middleRightSplit.Panel1MinSize)
                        _middleRightSplit.SplitterDistance = innerSplitterDist;
                }

                UpdateToggleButtonPosition();
            }
            catch (Exception ex)
            {
                // Loglama yapabilirsiniz
            }
            finally
            {
                this.ResumeLayout(true); // Çizimi tamamla
            }
        }

        // Oturum kontrolü (token veya cookie varlığı)
        private bool IsLoggedIn()
        {
            try
            {
                if (!string.IsNullOrEmpty(_authToken)) return true;
                if (_cookieContainer != null && _serverBase != null)
                {
                    var cookies = _cookieContainer.GetCookies(_serverBase);
                    return cookies != null && cookies.Count > 0;
                }
            }
            catch { }
            return false;
        }

        public void Activate() { }

        public void Deactivate()
        {
            try
            {
                // AnafartaTabFunctions kaldırıldı; önceki Deactivate çağrısı buradan temizlendi.
                AppendLogInternal("Deactivate çağrıldı");

                // stop telemetry on deactivate
                _isFetching = false;
                StopTelemetryTimer();
                _httpClient?.Dispose();
                _httpClient = null;
            }
            catch { }
        }
    }
}