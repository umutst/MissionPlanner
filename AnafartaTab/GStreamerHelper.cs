using System;
using System.Drawing;
using System.Windows.Forms;
using log4net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using VlcCore = LibVLCSharp.Shared.Core;
using VlcLib = LibVLCSharp.Shared.LibVLC;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace gst
{
    public class GStreamerHelper
    {
        private readonly ILog log = LogManager.GetLogger(typeof(GStreamerHelper));

        private TableLayoutPanel _layoutRoot;
        private Panel _controlsPanel;
        private TextBox _txtRtspUrl;
        private Button _btnPlay;
        private Button _btnStop;
        private VideoView _videoView;
        private VideoContainerPanel _videoContainer;

        private VlcLib _libVLC;
        private VlcMediaPlayer _mediaPlayer;
        private bool _libVlcInitialized;

        // HUD geniþliði baþlangýçta uygulanacak
        private int? _initialHudWidth;

        // Özel panel: ilk handle oluþumunda ve her resize’da doldurmayý garanti eder
        private class VideoContainerPanel : Panel
        {
            public VideoView VideoView { get; set; }
            public VlcMediaPlayer MediaPlayer { get; set; }
            public Action ForceFillCallback { get; set; }

            public VideoContainerPanel()
            {
                DoubleBuffered = true;
                this.Margin = new System.Windows.Forms.Padding(0);
                this.Padding = new System.Windows.Forms.Padding(0);
                this.BorderStyle = BorderStyle.None;
                this.BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint, true);
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                MinimumSize = new Size(100, 60);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ForceFillCallback?.Invoke();
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                ForceFillCallback?.Invoke();
            }
        }

        // UYUMLULUK için 3 parametreli imza: parentControl geniþliðini baþlangýçta uygular
        public Panel InitializeGStreamerPanel(Control parentControl, Control tabControlActions, SplitContainer subMainLeft)
        {
            if (parentControl != null)
                _initialHudWidth = parentControl.ClientSize.Width;
            var ctrl = InitializeGStreamerPanel(tabControlActions, subMainLeft);
            return ctrl as Panel ?? _layoutRoot;
        }

        public Control InitializeGStreamerPanel(Control tabControlActions, SplitContainer subMainLeft)
        {
            _layoutRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(15, 15, 15),
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            _layoutRoot.Margin = new System.Windows.Forms.Padding(0);
            _layoutRoot.Padding = new System.Windows.Forms.Padding(0);
            _layoutRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            _layoutRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(35, 35, 35)
            };
            _controlsPanel.Margin = new System.Windows.Forms.Padding(0);
            _controlsPanel.Padding = new System.Windows.Forms.Padding(0);

            _txtRtspUrl = new TextBox
            {
                Text = "rtsp://localhost:554/live",
                Location = new Point(6, 6),
                Width = 260,
                BackColor = Color.Black,
                ForeColor = Color.Lime
            };

            _btnPlay = new Button
            {
                Text = "Baþlat",
                Location = new Point(_txtRtspUrl.Right + 8, 4),
                Height = 24,
                Width = 70,
                BackColor = Color.Gray,
                ForeColor = Color.White
            };
            _btnPlay.Click += (s, e) => StartStream(_txtRtspUrl.Text);

            _btnStop = new Button
            {
                Text = "Durdur",
                Location = new Point(_btnPlay.Right + 6, 4),
                Height = 24,
                Width = 70,
                BackColor = Color.Gray,
                ForeColor = Color.White
            };
            _btnStop.Click += (s, e) => StopStream();

            _controlsPanel.Controls.Add(_txtRtspUrl);
            _controlsPanel.Controls.Add(_btnPlay);
            _controlsPanel.Controls.Add(_btnStop);

            _videoContainer = new VideoContainerPanel
            {
                Dock = DockStyle.Fill
            };

            _videoView = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            _videoContainer.VideoView = _videoView;
            _videoContainer.Controls.Add(_videoView);
            _videoContainer.ForceFillCallback = ForceFill;

            // Baþlangýç geniþliðini HUD ile eþitle (varsa)
            if (_initialHudWidth.HasValue && _initialHudWidth.Value > 0)
            {
                // Layout oluþturulmadan önce geniþliði uygula
                _layoutRoot.Width = _initialHudWidth.Value;
                _videoContainer.Width = _initialHudWidth.Value;
            }

            _layoutRoot.SuspendLayout();
            _layoutRoot.Controls.Add(_controlsPanel, 0, 0);
            _layoutRoot.Controls.Add(_videoContainer, 0, 1);
            _layoutRoot.ResumeLayout(true);

            if (subMainLeft != null && tabControlActions != null)
            {
                var middleSplit = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterDistance = 320,
                    Panel1MinSize = 80,
                    Panel2MinSize = 80,
                    BackColor = Color.FromArgb(25, 25, 25)
                };

                tabControlActions.Dock = DockStyle.Fill;
                middleSplit.Panel2.Controls.Add(tabControlActions);

                middleSplit.Panel1.Controls.Add(_layoutRoot);
                _layoutRoot.Dock = DockStyle.Fill;

                subMainLeft.Panel2.Controls.Add(middleSplit);
            }

            return _layoutRoot;
        }

        // Dýþarýdan HUD geniþliðini set etmek için (ör. FlightData.cs’den)
        public void SetInitialHudWidth(int hudWidth)
        {
            if (hudWidth <= 0) return;
            _initialHudWidth = hudWidth;

            // Açýkken çaðrýlýrsa anýnda uygula
            if (_layoutRoot != null)
            {
                _layoutRoot.Width = hudWidth;
            }
            if (_videoContainer != null)
            {
                _videoContainer.Width = hudWidth;
            }
        }

        private void StartStream(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("RTSP URL boþ.");
                return;
            }

            try
            {
                if (!_libVlcInitialized)
                {
                    VlcCore.Initialize();

                    string[] vlcArgs =
                    {
                        "--no-video-title-show",
                        "--quiet",
                        "--no-osd",
                        "--rtsp-tcp"
                    };

                    _libVLC = new VlcLib(vlcArgs);
                    _mediaPlayer = new VlcMediaPlayer(_libVLC);
                    _videoView.MediaPlayer = _mediaPlayer;
                    _videoContainer.MediaPlayer = _mediaPlayer;
                    _libVlcInitialized = true;

                    _mediaPlayer.Playing += (s, e) => ForceFill();
                }

                using (var media = new VlcMedia(_libVLC, url, FromType.FromLocation))
                {
                    _mediaPlayer.Media = media;
                }

                _mediaPlayer.AspectRatio = null;
                _mediaPlayer.Scale = 0;
                _mediaPlayer.CropGeometry = null;
                _mediaPlayer.Play();

                log.Info("Akýþ baþlatýldý: " + url);
            }
            catch (DllNotFoundException ex)
            {
                log.Error("libvlc DLL bulunamadý: " + ex.Message, ex);
                MessageBox.Show("libvlc bulunamadý. LibVLCSharp.WinForms + VideoLAN.LibVLC paketi kurulu mu?");
            }
            catch (Exception ex)
            {
                log.Error("Baþlatma hatasý: " + ex.Message, ex);
                MessageBox.Show("Baþlatma hatasý: " + ex.Message);
            }
        }

        private void ForceFill()
        {
            try
            {
                if (_mediaPlayer == null || !_mediaPlayer.IsPlaying || _videoContainer == null)
                    return;

                int w = _videoContainer.Width;
                int h = _videoContainer.Height;
                if (w <= 0 || h <= 0)
                    return;

                string forced = $"{w}:{h}";
                if (_mediaPlayer.AspectRatio != forced)
                    _mediaPlayer.AspectRatio = forced;

                _mediaPlayer.Scale = 0;
                _mediaPlayer.CropGeometry = null;
            }
            catch (Exception ex)
            {
                log.Warn("Doldurma hatasý: " + ex.Message);
            }
        }

        private void StopStream()
        {
            try
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                _mediaPlayer = null;

                _libVLC?.Dispose();
                _libVLC = null;
                _libVlcInitialized = false;
            }
            catch (Exception ex)
            {
                log.Error("Durdurma hatasý: " + ex.Message, ex);
            }
        }
    }
}