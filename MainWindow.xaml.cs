using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace AmalurBxmlConverter
{
    public partial class MainWindow : Window
    {
        private readonly BxmlEngine _engine = new();
        private const string ConfigFile = "config.txt";

        public MainWindow()
        {
            InitializeComponent();
            InitializeDatabasePath();
        }

        private async void InitializeDatabasePath()
        {
            if (File.Exists(ConfigFile))
            {
                string savedPath = File.ReadAllText(ConfigFile).Trim();
                if (Directory.Exists(savedPath))
                {
                    TxtCsvPath.Text = savedPath;
                    await TriggerDatabaseLoad(savedPath);
                    return;
                }
            }

            string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "symbol_tables");
            TxtCsvPath.Text = defaultPath;

            if (Directory.Exists(defaultPath))
                await TriggerDatabaseLoad(defaultPath);
            else
            {
                TxtStatusMain.Text = "Database Idle";
                TxtStatusSub.Text = "Place 'symbol_tables' next to .exe or browse manually.";
            }
        }

        private async void BtnBrowseCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select game symbol_tables Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                string selectedPath = dialog.FolderName;
                TxtCsvPath.Text = selectedPath;
                File.WriteAllText(ConfigFile, selectedPath);
                await TriggerDatabaseLoad(selectedPath);
            }
        }

private async Task TriggerDatabaseLoad(string path)
        {
            TxtStatusMain.Text = "Loading Database...";
            TxtStatusSub.Text = "Reading symbol tables...";

            try
            {
                await Task.Run(() => _engine.LoadGlobalDictionaries(path));
                TxtStatusMain.Text = "Database Loaded!";
                TxtStatusSub.Text = "Ready to accept dragged files.";
                
                // This line will now tell you exactly how many unique words you have.
                TxtLog.AppendText($"[INFO] Successfully mapped {_engine.LoadedSymbolCount:N0} unique symbols.\n");
            }
            catch (Exception ex)
            {
                TxtStatusMain.Text = "Load Failed";
                TxtStatusSub.Text = "Check your directory selection.";
                TxtLog.AppendText($"[ERROR] {ex.Message}\n");
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && _engine.IsDatabaseLoaded)
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
                
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            string filePath = files[0];
            
            if (IsXmlFile(filePath))
                await RunCompile(filePath);
            else
                await RunDecompile(filePath);
        }

        private bool IsXmlFile(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                
                char[] buffer = new char[20];
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0) return false;

                // Trim UTF-8 BOM marks and whitespace characters
                string heading = new string(buffer, 0, read).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                return heading.StartsWith("<");
            }
            catch
            {
                return false;
            }
        }

        private async Task RunDecompile(string binaryPath)
        {
            TxtStatusMain.Text = "Decompiling...";
            TxtStatusSub.Text = Path.GetFileName(binaryPath);
            TxtLog.AppendText($"[INFO] Processing Binary: {binaryPath}\n");

            string outXml = binaryPath + ".xml";

            try
            {
                await Task.Run(() => 
                {
                    var doc = _engine.Decompile(binaryPath);
                    doc.Save(outXml);
                });
                
                TxtStatusMain.Text = "Decompiled Successfully!";
                TxtStatusSub.Text = $"Saved as: {Path.GetFileName(outXml)}";
                TxtLog.AppendText($"[SUCCESS] Output saved to: {outXml}\n");
            }
            catch (Exception ex)
            {
                TxtStatusMain.Text = "Decompile Failed";
                TxtStatusSub.Text = "Invalid file structure.";
                TxtLog.AppendText($"[ERROR] {ex.Message}\n");
            }
        }

        private async Task RunCompile(string xmlPath)
        {
            TxtStatusMain.Text = "Compiling...";
            TxtStatusSub.Text = Path.GetFileName(xmlPath);
            TxtLog.AppendText($"[INFO] Compiling XML: {xmlPath}\n");

            string outBinary;
            
            if (xmlPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = xmlPath.Substring(0, xmlPath.Length - 4);
                if (string.IsNullOrEmpty(Path.GetExtension(stripped)))
                    stripped += ".bxml";
                    
                outBinary = stripped;
            }
            else
            {
                outBinary = xmlPath + ".bxml";
            }

            try
            {
                await Task.Run(() => _engine.Compile(xmlPath, outBinary));
                
                TxtStatusMain.Text = "Compiled Successfully!";
                TxtStatusSub.Text = $"Saved as: {Path.GetFileName(outBinary)}";
                TxtLog.AppendText($"[SUCCESS] Output saved to: {outBinary}\n");
            }
            catch (Exception ex)
            {
                TxtStatusMain.Text = "Compile Failed";
                TxtStatusSub.Text = "Review XML layout syntax.";
                TxtLog.AppendText($"[ERROR] {ex.Message}\n");
            }
        }
    }
}