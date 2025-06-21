using System;
using System.Text;
using System.Windows;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace blockwise
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        //private readonly TextBoxOutputter outputter;
        private static DispatcherTimer? elapsedTimer;
        private static readonly Stopwatch? stopWatch = new();
        private CancellationTokenSource? _cancellationTokenSource;
        public MainWindow()
        {
            InitializeComponent();
            //outputter = new TextBoxOutputter(OutputConsole);
            Console.SetOut(new TextBoxOutputter(OutputConsole));
            elapsedTimer = new DispatcherTimer();
            elapsedTimer.Tick += new EventHandler(ElapsedTime!);
            elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        }
        private void ElapsedTime(object source, EventArgs e)
        {

            TimeSpan timeSpan = stopWatch!.Elapsed;
            string elapsedTime = String.Format("{0:00}:{1:00}:{2:00}", timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds);
            TimerLabel.Content = elapsedTime;

        }
        public class TextBoxOutputter(TextBox output) : TextWriter
        {
            private readonly TextBox textBox = output;

            public override void Write(string? value)
            {
                if (string.IsNullOrEmpty(value)) return;

                textBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    textBox.AppendText(value);
                    textBox.CaretIndex = textBox.Text.Length;
                    textBox.ScrollToEnd();
                    textBox.IsReadOnly = true;
                }));
            }

            public override void WriteLine(string? value)
            {
                Write(value + Environment.NewLine);
            }

            public override Encoding Encoding => Encoding.UTF8;
        }

        public static string GenerateMD5(byte[] input)
        {
            byte[] hash = MD5.HashData(input);
            string md5Hash = (BitConverter.ToString(hash)).Replace("-","");
            return md5Hash;
        }
        public static string GenerateSHA1(byte[] input)
        {
            byte[] hash = SHA1.HashData(input);
            string sha1Hash = (BitConverter.ToString(hash)).Replace("-", "");
            return sha1Hash;
        }
        public static string GenerateSHA256(byte[] input)
        {
            byte[] hash = SHA256.HashData(input);
            string sha256Hash = (BitConverter.ToString(hash)).Replace("-", "");
            return sha256Hash;
        }
        private void BeginHashingFolder(object sender, RoutedEventArgs e)
        {
            OpenDirectory();
        }
        private async void OpenDirectory()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            StopButton.IsEnabled = true;

            OutputConsole.Clear();
            var openDirectory = new OpenFolderDialog
            {
                Title = "Choose a folder",
                Multiselect = false,
                ShowHiddenItems = false,
            };
            
            if (openDirectory.ShowDialog() == true)
            {
                stopWatch?.Reset();
                stopWatch?.Start();
                elapsedTimer?.Start();
                var recurseChoice = SearchOption.TopDirectoryOnly;
                var recursive = MessageBox.Show("Would you like to load the directory recursively?", "Recursive?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (recursive == MessageBoxResult.Yes)
                {
                    recurseChoice = SearchOption.AllDirectories;
                }
                string dirPath = openDirectory.FolderName;
                var files = Directory.GetFiles(dirPath, "*", recurseChoice);
                try
                {
                    if (files.Length > 0)
                    {

                        await HashFileBlocks(files, dirPath, _cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    MessageBox.Show("Hashing was canceled.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    StopButton.IsEnabled = false;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }
        private void BeginHashingFiles(object sender, RoutedEventArgs e)
        {
            OpenFiles();
        }
        private async void OpenFiles()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            StopButton.IsEnabled = true;
            OutputConsole.Clear();
            var openFiles = new OpenFileDialog
            {
                Title = "Select a File or Files",
                Filter = "All files (*.*)|*.*",
                Multiselect = true
            };
            if (openFiles.ShowDialog() == true)
            {
                stopWatch?.Reset();
                stopWatch?.Start();
                elapsedTimer?.Start();
                string[] selectedFiles = openFiles.FileNames;
                try
                {
                    if (selectedFiles.Length > 0)
                    {
                        string dirPath = Path.GetDirectoryName(openFiles.FileName)!;

                        await HashFileBlocks(selectedFiles, dirPath, _cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    MessageBox.Show("Hashing was canceled.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    StopButton.IsEnabled = false;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }
        private void DeduplicateHashList(string fileName)
        {
            //Need to load file and deduplicate the lines.
        }
        private async Task FlushToConsole(string output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) 
                    { 
                    Console.WriteLine(output);
                    }
                }, DispatcherPriority.Background);
            }
        }
        public async Task HashFileBlocks(string[] filePaths, string outputDirectory, CancellationToken token)
        {
            int count = 0;
            if (BlockSize.SelectedItem is not ComboBoxItem selectedItem ||
                !int.TryParse(selectedItem.Content.ToString(), out int blockSize) ||
                blockSize <= 0)
            {
                MessageBox.Show("Invalid block size selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (Hashes.Text == "Hash Type")
            {
                MessageBox.Show("You must select a Hash Type from the drop-down.");
                return;
            }

            string outputFile = Path.Combine(outputDirectory, "hashes.hsh");
            string hashType = Hashes.Text;
            StreamWriter? writer = null;
            StringBuilder batchOutput = new();
            try
            {

                //await using StreamWriter writer = new(outputFile, append: false);
                writer = new StreamWriter(outputFile, append: false);
                await writer.WriteLineAsync(hashType);

                
                byte[] buffer = new byte[blockSize];

                foreach (var filePath in filePaths)
                {
                    token.ThrowIfCancellationRequested();
                    await using FileStream fs = new(
                        filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);

                    int bytesRead;
                    while ((bytesRead = await fs.ReadAsync(buffer, token)) == blockSize)
                    {
                        token.ThrowIfCancellationRequested();
                        string hash = hashType switch
                        {
                            "MD5" => GenerateMD5(buffer),
                            "SHA1" => GenerateSHA1(buffer),
                            "SHA256" => GenerateSHA256(buffer),
                            _ => throw new InvalidOperationException("Unsupported hash type.")
                        };
                        token.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(hash) && !token.IsCancellationRequested)
                        {
                            await writer.WriteLineAsync(hash);
                            batchOutput.AppendLine(hash);
                            count++;
                            CounterLabel.Content = $"{count} hashes";
                        }
                        if (batchOutput.Length > 5000 && !token.IsCancellationRequested)
                        {
                            await FlushToConsole(batchOutput.ToString().TrimEnd('\r','\n'), token);
                            batchOutput.Clear();
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                if (batchOutput.Length > 0 && !token.IsCancellationRequested)
                {
                    await FlushToConsole(batchOutput.ToString().TrimEnd('\r','\n'), token);
                }

                stopWatch?.Stop();
                elapsedTimer?.Stop();
                MessageBox.Show("Hashing complete.", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                batchOutput.Clear();
                writer?.Close();
                writer = null;

                MessageBox.Show("Hashing was canceled.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error writing output file: {ex.Message}", "IO Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally 
            {
                writer?.Dispose();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                stopWatch?.Stop();
                elapsedTimer?.Stop();
            }
        }
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
        }
    }
}