using iTextSharp.text.pdf;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;

namespace FaPiao
    {
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            _execute(parameter);
        }
    }

    public class ImportedFile
    {
        public required string FileName { get; set; }
        public required string FilePath { get; set; }
        public required ICommand DeleteCommand { get; set; }
    }

    public class VisualHost : FrameworkElement
    {
        private Visual _visual;
        protected override int VisualChildrenCount => 1;

        public Visual Visual
        {
            get { return _visual; }
            set
            {
                _visual = value;
                AddVisualChild(value);
            }
        }

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _visual;
        }
    }

    public partial class MainWindow : Window
    {
        private ObservableCollection<ImportedFile> importedFiles = new();
        private string? currentFilePath;
        private int printCopies = 1;
        private bool printBackground = true;
        private bool printBorder = true;
        private bool centerOnPage = true;
        
        // A4纸张尺寸（像素，按96DPI计算）
        private const double A4_WIDTH_MM = 210;
        private const double A4_HEIGHT_MM = 297;
        private const double MM_TO_PIXEL = 3.779528; // 1毫米 = 3.779528像素（96DPI）
        private double pageWidth = A4_WIDTH_MM * MM_TO_PIXEL;
        private double pageHeight = A4_HEIGHT_MM * MM_TO_PIXEL;
        
        private bool isLandscape = false;
        private bool isDoublePage = false;
        private bool isFourTickets = false;
        private double currentZoom = 1.0;

        // 票据类型
        private enum TicketType
        {
            EInvoice,    // 增值税电子发票
            TrainTicket, // 火车票
            FlightItinerary, // 飞机行程单
            TaxiInvoice,    // 出租车发票
            OtherTicket     // 其他票据
        }

        // 票据尺寸配置（单位：毫米）
        private class TicketSize
        {
            public double Width { get; set; }
            public double Height { get; set; }
            public bool IsLandscape { get; set; }
        }

        private readonly Dictionary<TicketType, TicketSize> ticketSizes = new Dictionary<TicketType, TicketSize>
        {
            { TicketType.EInvoice, new TicketSize { Width = 210, Height = 140, IsLandscape = true } },
            { TicketType.TrainTicket, new TicketSize { Width = 54, Height = 143, IsLandscape = false } },
            { TicketType.FlightItinerary, new TicketSize { Width = 210, Height = 100, IsLandscape = true } },
            { TicketType.TaxiInvoice, new TicketSize { Width = 80, Height = 150, IsLandscape = false } },
            { TicketType.OtherTicket, new TicketSize { Width = 210, Height = 297, IsLandscape = false } }
        };

        private TicketType currentTicketType = TicketType.EInvoice;

        // 图片质量设置
        private class ImageQualitySettings
        {
            public int Dpi { get; set; }
            public int JpegQuality { get; set; }
            public bool EnableImageOptimization { get; set; }
        }

        private ImageQualitySettings qualitySettings = new ImageQualitySettings
        {
            Dpi = 300,
            JpegQuality = 90,
            EnableImageOptimization = true
        };

        public MainWindow()
        {
            try
            {
                // 首先初始化界面
                InitializeComponent();

                // 初始化数据
                importedFiles = new ObservableCollection<ImportedFile>();
                FileListBox.ItemsSource = importedFiles;

                // 设置预览画布的初始大小为A4
                UpdateCanvasSize();

                // 绑定事件处理器
                ImportButton.Click += ImportButton_Click;
                FileListBox.SelectionChanged += FileListBox_SelectionChanged;
                PrintButton.Click += PrintButton_Click;

                // 初始化控件状态
                InitializeControls();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeControls()
        {
            try
            {
                // 设置默认的票据类型
                if (TicketTypeComboBox != null)
                {
                    TicketTypeComboBox.SelectedIndex = 0;
                }

                // 设置默认的DPI选项
                if (DpiComboBox != null)
                {
                    DpiComboBox.SelectedIndex = 1; // 300 DPI
                }

                // 设置默认的图片质量
                if (QualitySlider != null)
                {
                    QualitySlider.Value = 90;
                }

                // 设置默认的缩放选项
                if (ZoomComboBox != null)
                {
                    ZoomComboBox.SelectedIndex = 1; // 100%
                }

                // 设置默认的布局选项
                var defaultLayoutRadio = this.FindName("SinglePortraitRadio") as RadioButton;
                if (defaultLayoutRadio != null)
                {
                    defaultLayoutRadio.IsChecked = true;
                }

                // 更新布局选项
                UpdateLayoutOptions();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化控件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateLayoutOptions()
        {
            try
            {
                var layoutPanel = this.FindName("LayoutPanel") as WrapPanel;
                if (layoutPanel == null) return;

                // 获取布局选项
                var fourTicketsRadio = this.FindName("FourTicketsRadio") as RadioButton;
                var fourTicketsLandscapeRadio = this.FindName("FourTicketsLandscapeRadio") as RadioButton;

                if (fourTicketsRadio != null && fourTicketsLandscapeRadio != null)
                {
                    // 根据票据类型显示或隐藏四联布局项
                    bool showFourTickets = currentTicketType == TicketType.TrainTicket || 
                                         currentTicketType == TicketType.TaxiInvoice;
                    
                    fourTicketsRadio.Visibility = showFourTickets ? Visibility.Visible : Visibility.Collapsed;
                    fourTicketsLandscapeRadio.Visibility = showFourTickets ? Visibility.Visible : Visibility.Collapsed;

                    // 如果当前选中的是四联布局，但切换到他票据类型，则重置为单张布局
                    if (!showFourTickets && 
                        (fourTicketsRadio.IsChecked == true || fourTicketsLandscapeRadio.IsChecked == true))
                    {
                        var singlePortraitRadio = this.FindName("SinglePortraitRadio") as RadioButton;
                        if (singlePortraitRadio != null)
                        {
                            singlePortraitRadio.IsChecked = true;
                        }
                    }
                }

                // 根据票据类型设置默认布局
                var ticketSize = ticketSizes[currentTicketType];
                if (ticketSize.IsLandscape)
                {
                    // 如果票据是横向的，自动选择横向布局
                    var singleLandscapeRadio = this.FindName("SingleLandscapeRadio") as RadioButton;
                    if (singleLandscapeRadio != null)
                    {
                        singleLandscapeRadio.IsChecked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"更新布局选项失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateCanvasSize()
        {
            if (PreviewCanvas != null && PreviewBorder != null)
            {
                // 确保画布尺寸有效
                PreviewCanvas.Width = Math.Max(1, pageWidth);
                PreviewCanvas.Height = Math.Max(1, pageHeight);
                PreviewBorder.Width = Math.Max(1, pageWidth);
                PreviewBorder.Height = Math.Max(1, pageHeight);

                PreviewCanvas.Background = Brushes.White;
            }
        }

        private void TicketTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TicketTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                // 更新当前票据类型
                currentTicketType = GetTicketTypeFromString(selectedItem.Content.ToString());

                // 更新布局选项
                UpdateLayoutOptions();

                // 清除已导入的文件
                importedFiles.Clear();
                ClearPreview();
            }
        }

        private TicketType GetTicketTypeFromString(string? typeName)
        {
            if (typeName == null) return TicketType.OtherTicket;
            
            return typeName switch
            {
                "增值税电子发票" => TicketType.EInvoice,
                "火车票" => TicketType.TrainTicket,
                "飞机行程单" => TicketType.FlightItinerary,
                "出��车发票" => TicketType.TaxiInvoice,
                _ => TicketType.OtherTicket
            };
        }

        private BitmapImage LoadImage(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return null;

                string extension = Path.GetExtension(filePath).ToLower();
                if (extension == ".pdf")
                {
                    return ConvertPdfToImage(filePath);
                }
                else
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载图片失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private BitmapImage ConvertPdfToImage(string pdfPath)
        {
            try
            {
                using (var reader = new PdfReader(pdfPath))
                {
                    if (reader.NumberOfPages < 1)
                        return null;

                    // 创建一个临时文件来存储转换后的图像
                    string tempImagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".png");

                    try
                    {
                        // 使用iTextSharp提取第一页
                        using (var document = new iTextSharp.text.Document())
                        using (var stream = new FileStream(tempImagePath, FileMode.Create))
                        {
                            var writer = PdfWriter.GetInstance(document, stream);
                            document.Open();

                            // 获取第一页
                            var page = writer.GetImportedPage(reader, 1);
                            var contentByte = writer.DirectContent;
                            contentByte.AddTemplate(page, 0, 0);

                            document.Close();
                        }

                        // 加载生成的图像
                        using (var stream = new FileStream(tempImagePath, FileMode.Open, FileAccess.Read))
                        {
                            var image = new BitmapImage();
                            image.BeginInit();
                            image.CacheOption = BitmapCacheOption.OnLoad;
                            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                            image.StreamSource = stream;
                            image.EndInit();
                            image.Freeze();
                            return image;
                        }
                    }
                    finally
                    {
                        // 清理临时文件
                        if (File.Exists(tempImagePath))
                        {
                            try { File.Delete(tempImagePath); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"PDF转换失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private System.Drawing.Image OptimizeImage(System.Drawing.Image originalImage)
        {
            // 创建新的位图
            var bitmap = new System.Drawing.Bitmap(originalImage.Width, originalImage.Height);
            
            // 设置分辨率
            bitmap.SetResolution(qualitySettings.Dpi, qualitySettings.Dpi);

            // 使用高质量绘制
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                // 绘制图像
                graphics.DrawImage(originalImage, 0, 0, bitmap.Width, bitmap.Height);
            }

            return bitmap;
        }

        private BitmapImage ConvertDrawingImageToBitmapImage(System.Drawing.Image image)
        {
            using (var memory = new MemoryStream())
            {
                // 保存为JPEG，设质量
                var jpegEncoder = GetEncoderInfo("image/jpeg");
                var encoderParameters = new EncoderParameters(1);
                encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, qualitySettings.JpegQuality);
                
                image.Save(memory, jpegEncoder, encoderParameters);
                memory.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = memory;
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // 使图片以跨线程使用

                return bitmapImage;
            }
        }

        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            return codecs.FirstOrDefault(codec => codec.MimeType == mimeType);
        }

        private void PreviewImage()
        {
            if (string.IsNullOrEmpty(currentFilePath) || !File.Exists(currentFilePath))
            {
                return;
            }

            try
            {
                // 检查控件是否已初始化
                if (PreviewCanvas == null || PreviewScrollViewer == null)
                {
                    return;
                }

                // 清除画布上现有内容
                PreviewCanvas.Children.Clear();

                // 获取当前票据类型的尺寸
                var ticketSize = ticketSizes[currentTicketType];

                // 计算可以在一页上放置的最大票据数
                int maxTicketsPerPage = CalculateMaxTicketsPerPage(ticketSize);

                // 获取要显示的票据
                var ticketsToShow = GetTicketsToShow(maxTicketsPerPage);
                if (ticketsToShow.Count > 0)
                {
                    // 计算单个票据的尺寸
                    double spacing = 20; // 票据之间的间距

                    if (ticketsToShow.Count == 1)
                    {
                        // 单张布局
                        double ticketWidth = pageWidth - 2 * spacing;
                        double ticketHeight = pageHeight - 2 * spacing;

                        var border = CreateTicketBorder(ticketsToShow[0], ticketWidth, ticketHeight);
                        Canvas.SetLeft(border, spacing);
                        Canvas.SetTop(border, spacing);
                        PreviewCanvas.Children.Add(border);
                    }
                    else if (ticketsToShow.Count == 2)
                    {
                        if (isLandscape)
                        {
                            // 横向双张
                            double ticketWidth = (pageWidth - 3 * spacing) / 2;
                            double ticketHeight = pageHeight - 2 * spacing;

                            for (int i = 0; i < 2; i++)
                            {
                                var border = CreateTicketBorder(ticketsToShow[i], ticketWidth, ticketHeight);
                                Canvas.SetLeft(border, i * (ticketWidth + spacing) + spacing);
                                Canvas.SetTop(border, spacing);
                                PreviewCanvas.Children.Add(border);
                            }
                        }
                        else
                        {
                            // 纵向双张
                            double ticketWidth = pageWidth - 2 * spacing;
                            double ticketHeight = (pageHeight - 3 * spacing) / 2;

                            for (int i = 0; i < 2; i++)
                            {
                                var border = CreateTicketBorder(ticketsToShow[i], ticketWidth, ticketHeight);
                                Canvas.SetLeft(border, spacing);
                                Canvas.SetTop(border, i * (ticketHeight + spacing) + spacing);
                                PreviewCanvas.Children.Add(border);
                            }
                        }
                    }
                    else if (ticketsToShow.Count >= 4)
                    {
                        // 四张布局
                        double ticketWidth = (pageWidth - 3 * spacing) / 2;
                        double ticketHeight = (pageHeight - 3 * spacing) / 2;

                        for (int i = 0; i < Math.Min(4, ticketsToShow.Count); i++)
                        {
                            var border = CreateTicketBorder(ticketsToShow[i], ticketWidth, ticketHeight);
                            double left = (i % 2 == 0) ? spacing : ticketWidth + 2 * spacing;
                            double top = (i < 2) ? spacing : ticketHeight + 2 * spacing;
                            Canvas.SetLeft(border, left);
                            Canvas.SetTop(border, top);
                            PreviewCanvas.Children.Add(border);
                        }
                    }
                }

                // 应用缩放
                if (ZoomComboBox?.SelectedItem is ComboBoxItem selectedItem && 
                    selectedItem.Content.ToString() == "适应窗口")
                {
                    AutoFitPreview();
                }
                else
                {
                    PreviewScrollViewer.LayoutTransform = new ScaleTransform(currentZoom, currentZoom);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"预览失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearPreview()
        {
            if (PdfImage != null)
                PdfImage.Source = null;
            if (PreviewCanvas != null)
                PreviewCanvas.Children.Clear();
            
            currentZoom = 1.0;
        }

        private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileListBox?.SelectedItem is ImportedFile selectedFile)
            {
                currentFilePath = selectedFile.FilePath;
                if (currentFilePath != null)
                {
                    PreviewImage();
                }
            }
        }

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateCanvasSize();
            PreviewImage();
        }

        private void RotateRightButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateCanvasSize();
            PreviewImage();
        }

        private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && 
                comboBox.SelectedItem is ComboBoxItem selectedItem && 
                selectedItem.Content is string zoomText)
            {
                if (zoomText == "适应窗口")
                {
                    FitToWindow();
                }
                else
                {
                    // 解析百分比值
                    string percentStr = zoomText.TrimEnd('%');
                    if (double.TryParse(percentStr, out double percent))
                    {
                        currentZoom = percent / 100.0;
                        PreviewImage();
                    }
                }
            }
        }

        private void Margin_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(((TextBox)sender).Text, out _))
            {
                PreviewImage();
            }
        }

        private void DpiComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DpiComboBox?.SelectedItem is ComboBoxItem selectedItem && 
                selectedItem.Content is string dpiText)
            {
                string[] parts = dpiText.Split(' ');
                if (parts.Length > 0 && int.TryParse(parts[0], out int dpi))
                {
                    qualitySettings.Dpi = dpi;
                    PreviewImage();
                }
            }
        }

        private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (QualitySlider != null)
            {
                qualitySettings.JpegQuality = (int)QualitySlider.Value;
                PreviewImage();
            }
        }

        private void OptimizeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (OptimizeCheckBox != null)
            {
                qualitySettings.EnableImageOptimization = OptimizeCheckBox.IsChecked ?? true;
                PreviewImage();
            }
        }

        private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // 计算新的缩放比
                double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
                double newZoom = currentZoom + zoomDelta;

                // 限制缩放范围
                newZoom = Math.Max(Math.Min(newZoom, 2.0), 0.1);

                if (Math.Abs(newZoom - currentZoom) > 0.01)
                {
                    currentZoom = newZoom;
                    PreviewImage();
                }

                e.Handled = true;
            }
        }

        private void AutoFitPreview()
        {
            if (PreviewScrollViewer == null || PreviewCanvas == null) return;

            // 获取预览区域的大小（减去滚动条和边距的空间）
            double viewportWidth = PreviewScrollViewer.ViewportWidth - 40; // 减去左右边距
            double viewportHeight = PreviewScrollViewer.ViewportHeight - 40; // 减去上下边距

            // 获取内容的实际大小
            double contentWidth = PreviewCanvas.Width;
            double contentHeight = PreviewCanvas.Height;

            // 计算合适的缩放比例
            double scaleX = viewportWidth / contentWidth;
            double scaleY = viewportHeight / contentHeight;
            currentZoom = Math.Min(scaleX, scaleY);

            // 限制最小和最大缩放比例
            currentZoom = Math.Max(Math.Min(currentZoom, 2.0), 0.1);

            // 应用缩放
            PreviewScrollViewer.LayoutTransform = new ScaleTransform(currentZoom, currentZoom);

            // 更新缩放下拉框的选择
            UpdateZoomComboBox();
        }

        private void UpdateZoomComboBox()
        {
            if (ZoomComboBox == null) return;

            // 当前缩放例转换为百分比
            int zoomPercent = (int)(currentZoom * 100);

            // 查找最接近的预设
            foreach (ComboBoxItem? item in ZoomComboBox.Items)
            {
                if (item?.Content is string content && content == "适应窗口")
                {
                    ZoomComboBox.SelectedItem = item;
                    return;
                }
            }
        }

        private void FitToWindow()
        {
            AutoFitPreview();
        }

        private void LayoutRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.IsChecked == true)
            {
                string layout = radioButton.Content.ToString();
                isLandscape = layout.Contains("横向");
                isDoublePage = layout.Contains("双张");
                isFourTickets = layout.Contains("四张");

                // 显示或隐藏自定义尺寸面板
                if (CustomSizePanel != null)
                {
                    CustomSizePanel.Visibility = layout.Contains("自定义") ? 
                        Visibility.Visible : Visibility.Collapsed;
                }

                if (layout.Contains("自定义"))
                {
                    // 使用自定义尺寸
                    UpdateCustomPageSize();
                }
                else
                {
                    // 使用标准A4尺寸
                    if (isLandscape)
                    {
                        pageWidth = A4_HEIGHT_MM * MM_TO_PIXEL;
                        pageHeight = A4_WIDTH_MM * MM_TO_PIXEL;
                    }
                    else
                    {
                        pageWidth = A4_WIDTH_MM * MM_TO_PIXEL;
                        pageHeight = A4_HEIGHT_MM * MM_TO_PIXEL;
                    }
                }

                UpdateCanvasSize();
                PreviewImage();
            }
        }

        private void CustomSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomSizeRadio?.IsChecked == true)
            {
                UpdateCustomPageSize();
            }
        }

        private void UpdateCustomPageSize()
        {
            if (double.TryParse(CustomWidthBox?.Text, out double widthMm) &&
                double.TryParse(CustomHeightBox?.Text, out double heightMm))
            {
                // 确保尺寸在合理范围内
                widthMm = Math.Max(50, Math.Min(widthMm, 1000));
                heightMm = Math.Max(50, Math.Min(heightMm, 1000));

                // 更新文本框显示
                if (CustomWidthBox != null && CustomWidthBox.Text != widthMm.ToString())
                    CustomWidthBox.Text = widthMm.ToString();
                if (CustomHeightBox != null && CustomHeightBox.Text != heightMm.ToString())
                    CustomHeightBox.Text = heightMm.ToString();

                // 更新页面尺寸
                pageWidth = widthMm * MM_TO_PIXEL;
                pageHeight = heightMm * MM_TO_PIXEL;

                UpdateCanvasSize();
                PreviewImage();
            }
        }

        private void PaperSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PaperSizeComboBox?.SelectedItem is ComboBoxItem selectedItem)
            {
                string content = selectedItem.Content.ToString();
                if (content.Contains("自定义"))
                {
                    // 保持当前输入的值
                    return;
                }

                // 解析尺寸信息
                int startIndex = content.IndexOf('(') + 1;
                int endIndex = content.IndexOf(')');
                if (startIndex > 0 && endIndex > startIndex)
                {
                    string[] dimensions = content.Substring(startIndex, endIndex - startIndex)
                                               .Split('×');
                    if (dimensions.Length == 2)
                    {
                        if (double.TryParse(dimensions[0], out double width) &&
                            double.TryParse(dimensions[1], out double height))
                        {
                            CustomWidthBox.Text = width.ToString();
                            CustomHeightBox.Text = height.ToString();
                        }
                    }
                }
            }
        }

        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            if (importedFiles.Count == 0)
            {
                MessageBox.Show("请先导入要打印的文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() == true)
            {
                try
                {
                    // 设置打印方向
                    if (isLandscape)
                    {
                        printDialog.PrintTicket.PageOrientation = PageOrientation.Landscape;
                    }
                    else
                    {
                        printDialog.PrintTicket.PageOrientation = PageOrientation.Portrait;
                    }

                    // 设置打印质量
                    printDialog.PrintTicket.PageResolution = new PageResolution(qualitySettings.Dpi, qualitySettings.Dpi);

                    // 设置纸张大小
                    if (CustomSizeRadio?.IsChecked == true)
                    {
                        // 使用自定义纸张大小
                        if (double.TryParse(CustomWidthBox?.Text, out double widthMm) &&
                            double.TryParse(CustomHeightBox?.Text, out double heightMm))
                        {
                            printDialog.PrintTicket.PageMediaSize = new PageMediaSize(
                                widthMm / 25.4 * 96,  // 转换为英寸后再转为像素
                                heightMm / 25.4 * 96
                            );
                        }
                    }
                    else
                    {
                        // 使用标准A4纸
                        printDialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
                    }

                    // 设置打印份数
                    printDialog.PrintTicket.CopyCount = printCopies;

                    // 直接打印预览画布
                    if (PreviewCanvas != null)
                    {
                        // 创建一个新的画布用打印
                        var printCanvas = new Canvas
                        {
                            Width = PreviewCanvas.Width,
                            Height = PreviewCanvas.Height,
                            Background = printBackground ? Brushes.White : null
                        };

                        // 复制预览画布的内容
                        foreach (UIElement element in PreviewCanvas.Children)
                        {
                            if (element is Border border)
                            {
                                var newBorder = new Border
                                {
                                    Width = border.Width,
                                    Height = border.Height,
                                    Background = border.Background
                                };

                                if (border.Child is Image image)
                                {
                                    newBorder.Child = new Image
                                    {
                                        Source = image.Source,
                                        Width = image.Width,
                                        Height = image.Height,
                                        Stretch = image.Stretch
                                    };
                                }

                                Canvas.SetLeft(newBorder, Canvas.GetLeft(border));
                                Canvas.SetTop(newBorder, Canvas.GetTop(border));
                                printCanvas.Children.Add(newBorder);
                            }
                        }

                        // 打印画布
                        printDialog.PrintVisual(printCanvas, "打印发票");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打印失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private Border CreateTicketBorder(BitmapImage bitmap, double width, double height)
        {
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = printBackground ? Brushes.White : null,
                Child = new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                }
            };

            // 设置图像渲染质量
            RenderOptions.SetBitmapScalingMode(border.Child, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(border.Child, EdgeMode.Aliased);
            RenderOptions.SetCachingHint(border.Child, CachingHint.Cache);

            return border;
        }

        private void CopiesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(CopiesTextBox.Text, out int copies))
            {
                printCopies = Math.Max(1, Math.Min(copies, 99)); // 限制在1-99份之间
                if (printCopies.ToString() != CopiesTextBox.Text)
                {
                    CopiesTextBox.Text = printCopies.ToString();
                }
            }
            else
            {
                CopiesTextBox.Text = "1";
                printCopies = 1;
            }
        }

        private void PrintBackgroundCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            printBackground = PrintBackgroundCheckBox.IsChecked ?? true;
            PreviewImage();
        }

        private void PrintBorderCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            printBorder = PrintBorderCheckBox.IsChecked ?? true;
            PreviewImage();
        }

        private void CenterOnPageCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            centerOnPage = CenterOnPageCheckBox.IsChecked ?? true;
            PreviewImage();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = GetFileFilter(),
                Multiselect = true,
                Title = "选择票据文件"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // 首先处理PDF文件
                var pdfFiles = openFileDialog.FileNames
                    .Where(f => Path.GetExtension(f).ToLower() == ".pdf")
                    .OrderBy(f => Path.GetFileName(f));

                // 然后处理图片文件
                var imageFiles = openFileDialog.FileNames
                    .Where(f => Path.GetExtension(f).ToLower() != ".pdf")
                    .OrderBy(f => Path.GetFileName(f));

                // 合并文件列表，PDF优先
                var orderedFiles = pdfFiles.Concat(imageFiles);

                foreach (string filename in orderedFiles)
                {
                    try
                    {
                        if (!importedFiles.Any(f => f.FilePath == filename))
                        {
                            var file = new ImportedFile
                            {
                                FileName = Path.GetFileName(filename),
                                FilePath = filename,
                                DeleteCommand = new RelayCommand(param => RemoveFile((ImportedFile)param))
                            };
                            importedFiles.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"导入文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                if (importedFiles.Count > 0 && FileListBox.SelectedItem == null)
                {
                    FileListBox.SelectedItem = importedFiles[0];
                }
            }
        }

        private string GetFileFilter()
        {
            switch (currentTicketType)
            {
                case TicketType.EInvoice:
                    return "所有支持的文件|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff|PDF文件(*.pdf)|*.pdf|图片文件|*.jpg;*.jpeg;*.png;*.tif;*.tiff|所有文件|*.*";
                case TicketType.TrainTicket:
                case TicketType.FlightItinerary:
                case TicketType.TaxiInvoice:
                case TicketType.OtherTicket:
                    return "所有支持的文件|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff|PDF文件(*.pdf)|*.pdf|图片文件|*.jpg;*.jpeg;*.png;*.tif;*.tiff|所有文件|*.*";
                default:
                    return "所有文件|*.*";
            }
        }

        private void RemoveFile(ImportedFile? file)
        {
            if (file == null) return;
            importedFiles.Remove(file);
            if (FileListBox.SelectedItem == null && importedFiles.Count > 0)
            {
                FileListBox.SelectedItem = importedFiles[0];
            }
            else if (importedFiles.Count == 0)
            {
                ClearPreview();
            }
        }

        private int CalculateMaxTicketsPerPage(TicketSize ticketSize)
        {
            // 考虑页面边距
            double availableWidth = pageWidth - 40; // 左右各20像素边距
            double availableHeight = pageHeight - 40; // 上下各20像素边距

            // 将票据尺寸转换为像素
            double ticketWidthPixels = ticketSize.Width * MM_TO_PIXEL;
            double ticketHeightPixels = ticketSize.Height * MM_TO_PIXEL;

            if (isLandscape)
            {
                // 交换宽高
                (availableWidth, availableHeight) = (availableHeight, availableWidth);
            }

            // 算每行/��可以放置的票据数量
            int ticketsPerRow = (int)(availableWidth / (ticketWidthPixels + 20)); // 20像素间距
            int ticketsPerColumn = (int)(availableHeight / (ticketHeightPixels + 20));

            // 根据布局选项返回合适的数量
            if (isFourTickets)
                return 4;
            else if (isDoublePage)
                return 2;
            else
                return Math.Max(1, Math.Min(ticketsPerRow * ticketsPerColumn, 4)); // 最多4张
        }

        private List<BitmapImage> GetTicketsToShow(int maxCount)
        {
            var tickets = new List<BitmapImage>();
            int startIndex = FileListBox.SelectedIndex;
            if (startIndex < 0) return tickets;

            // 获取从选中项开始的指定数量的票据
            for (int i = 0; i < maxCount && startIndex + i < importedFiles.Count; i++)
            {
                var file = importedFiles[startIndex + i];
                var bitmap = LoadImage(file.FilePath);
                if (bitmap != null)
                {
                    tickets.Add(bitmap);
                }
            }

            return tickets;
        }

        private void SaveCanvasToImage(Canvas canvas, string filePath)
        {
            try
            {
                // 获取Canvas的实际大小
                double width = canvas.ActualWidth;
                double height = canvas.ActualHeight;
                if (width <= 0 || height <= 0)
                {
                    width = canvas.Width;
                    height = canvas.Height;
                }

                // 创建高分辨率的RenderTargetBitmap
                var renderBitmap = new RenderTargetBitmap(
                    (int)width,
                    (int)height,
                    96,
                    96,
                    PixelFormats.Pbgra32);

                // 创建DrawingVisual
                var drawingVisual = new DrawingVisual();
                using (DrawingContext dc = drawingVisual.RenderOpen())
                {
                    // 绘制背景
                    if (printBackground)
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    }

                    // 遍历并绘制所有元素
                    foreach (UIElement element in canvas.Children)
                    {
                        if (element is Border border && border.Child is Image image)
                        {
                            var left = Canvas.GetLeft(border);
                            var top = Canvas.GetTop(border);

                            // 获取图像源
                            if (image.Source is BitmapSource bitmapSource)
                            {
                                // 使用原始尺寸和位置
                                dc.DrawImage(bitmapSource, 
                                    new Rect(left, top, border.Width, border.Height));
                            }
                        }
                    }
                }

                // 渲染DrawingVisual
                renderBitmap.Render(drawingVisual);

                // 使用PNG编码器保存（无损压缩）
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                // 保存图片
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存图片失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportToPdf()
        {
            try
            {
                if (PreviewCanvas == null || PreviewCanvas.Children.Count == 0)
                {
                    MessageBox.Show("没有可导出的内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "PDF文件|*.pdf",
                    Title = "导出PDF",
                    DefaultExt = "pdf",
                    AddExtension = true
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    // 创建PDF文档
                    using (var fs = new FileStream(saveFileDialog.FileName, FileMode.Create))
                    {
                        // 创建没有边距的文档
                        var document = new iTextSharp.text.Document(
                            isLandscape ? iTextSharp.text.PageSize.A4.Rotate() : iTextSharp.text.PageSize.A4,
                            0, 0, 0, 0); // 设置所有边距为0

                        var writer = PdfWriter.GetInstance(document, fs);
                        document.Open();

                        // 创建一个DrawingVisual来渲染预览内容
                        var drawingVisual = new DrawingVisual();
                        using (DrawingContext dc = drawingVisual.RenderOpen())
                        {
                            // 绘制背景
                            if (printBackground)
                            {
                                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PreviewCanvas.Width, PreviewCanvas.Height));
                            }

                            // 遍历并绘制所有元素
                            foreach (UIElement element in PreviewCanvas.Children)
                            {
                                if (element is Border border && border.Child is Image image)
                                {
                                    var left = Canvas.GetLeft(border);
                                    var top = Canvas.GetTop(border);

                                    if (image.Source is BitmapSource bitmapSource)
                                    {
                                        dc.DrawImage(bitmapSource, new Rect(left, top, border.Width, border.Height));
                                    }
                                }
                            }
                        }

                        // 创建RenderTargetBitmap
                        var renderBitmap = new RenderTargetBitmap(
                            (int)PreviewCanvas.Width,
                            (int)PreviewCanvas.Height,
                            96, 96,
                            PixelFormats.Pbgra32);

                        renderBitmap.Render(drawingVisual);

                        // 将RenderTargetBitmap转换为byte数组
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));
                        using (var ms = new MemoryStream())
                        {
                            encoder.Save(ms);
                            ms.Position = 0;
                            var image = iTextSharp.text.Image.GetInstance(ms.ToArray());

                            // 设置图片大小为页面大小
                            image.ScaleToFit(document.PageSize.Width, document.PageSize.Height);

                            // 居中放置
                            float x = (document.PageSize.Width - image.ScaledWidth) / 2;
                            float y = (document.PageSize.Height - image.ScaledHeight) / 2;
                            image.SetAbsolutePosition(x, y);

                            document.Add(image);
                        }
                        document.Close();
                    }

                    MessageBox.Show("PDF导出成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出PDF失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportPdfButton_Click(object sender, RoutedEventArgs e)
        {
            ExportToPdf();
        }

        private List<ImportedFile> GetCurrentDisplayFiles()
        {
            var result = new List<ImportedFile>();
            if (FileListBox.SelectedItem is ImportedFile selectedFile)
            {
                int startIndex = FileListBox.SelectedIndex;
                int count = 1;

                // 根据布局确定需要的文件数量
                if (isFourTickets)
                    count = 4;
                else if (isDoublePage)
                    count = 2;

                // 获取从选中项开始的指定数量的文件
                for (int i = 0; i < count && startIndex + i < importedFiles.Count; i++)
                {
                    result.Add(importedFiles[startIndex + i]);
                }
            }
            return result;
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }
    }
}