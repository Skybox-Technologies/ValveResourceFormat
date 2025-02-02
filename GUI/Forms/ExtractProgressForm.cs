using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.Exporter;
using GUI.Utils;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace GUI.Forms
{
    partial class ExtractProgressForm : Form
    {
        private bool decompile;
        private readonly string path;
        private readonly ExportData exportData;
        private readonly Dictionary<string, Queue<PackageEntry>> filesToExtractSorted;
        private readonly Queue<PackageEntry> filesToExtract;
        private readonly HashSet<string> extractedFiles;
        private readonly GltfModelExporter gltfExporter;
        private CancellationTokenSource cancellationTokenSource;
        private Stopwatch exportStopwatch;

        private static readonly List<ResourceType> ExtractOrder = new()
        {
            ResourceType.Map,
            ResourceType.World,
            ResourceType.WorldNode,
            ResourceType.Model,
            ResourceType.Mesh,
            ResourceType.AnimationGroup,
            ResourceType.Animation,
            ResourceType.Sequence,
            ResourceType.Morph,

            ResourceType.Material,
            ResourceType.Texture,
        };

        public Action<ExtractProgressForm, CancellationToken> ShownCallback { get; init; }

        public ExtractProgressForm(ExportData exportData, string path, bool decompile)
        {
            InitializeComponent();

            cancellationTokenSource = new CancellationTokenSource();

            filesToExtractSorted = new();
            foreach (var resourceType in ExtractOrder)
            {
                var extension = FileExtract.GetExtension(resourceType);
                filesToExtractSorted.Add(extension + "_c", new Queue<PackageEntry>());
            }
            filesToExtract = new Queue<PackageEntry>();
            extractedFiles = new HashSet<string>();

            this.path = path;
            this.decompile = decompile;
            this.exportData = exportData;

            if (decompile)
            {
                // We need to know what files were handled by the glTF exporter
                var trackingFileLoader = new TrackingFileLoader(exportData.VrfGuiContext.FileLoader);

                gltfExporter = new GltfModelExporter(trackingFileLoader)
                {
                    ProgressReporter = new Progress<string>(SetProgress),
                };
            }
        }

        protected override void OnShown(EventArgs e)
        {
            exportStopwatch = Stopwatch.StartNew();

            if (ShownCallback != null)
            {
                extractProgressBar.Style = ProgressBarStyle.Marquee;
                ShownCallback(this, cancellationTokenSource.Token);
                return;
            }

            Task.Run(async () =>
            {
                SetProgress($"Folder export started to \"{path}\"");

                if (decompile)
                {
                    foreach (var resourceType in ExtractOrder)
                    {
                        var extension = FileExtract.GetExtension(resourceType);
                        var files = filesToExtractSorted[extension + "_c"];

                        if (files.Count > 0)
                        {
                            SetProgress($"Extracting {resourceType}s…");
                            await ExtractFilesAsync(files).ConfigureAwait(false);
                        }
                    }

                    if (filesToExtract.Count > 0)
                    {
                        SetProgress("Extracting files…");
                    }
                }

                await ExtractFilesAsync(filesToExtract).ConfigureAwait(false);
            }, cancellationTokenSource.Token).ContinueWith(ExportContinueWith, CancellationToken.None);
        }

        public void ExportContinueWith(Task t)
        {
            exportStopwatch.Stop();

            if (t.IsFaulted)
            {
                Console.Error.WriteLine(t.Exception);
                SetProgress(t.Exception.ToString());

                cancellationTokenSource.Cancel();
            }

            Invoke(() =>
            {
                Text = t.IsFaulted ? "Source 2 Viewer - Export failed, check console for details" : "Source 2 Viewer - Export completed";
                cancelButton.Text = "Close";
                extractProgressBar.Value = 100;
                extractProgressBar.Style = ProgressBarStyle.Blocks;
                extractProgressBar.Update();
            });

            SetProgress($"Export completed in {exportStopwatch.Elapsed}.");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            cancellationTokenSource.Cancel();
        }

        public void QueueFiles(BetterTreeNode root)
        {
            if (root.IsFolder)
            {
                foreach (BetterTreeNode node in root.Nodes)
                {
                    if (!node.IsFolder)
                    {
                        var file = node.PackageEntry;
                        if (decompile && filesToExtractSorted.TryGetValue(file.TypeName, out var specializedQueue))
                        {
                            specializedQueue.Enqueue(file);
                            continue;
                        }

                        filesToExtract.Enqueue(file);
                    }
                    else
                    {
                        QueueFiles(node);
                    }
                }
            }
            else
            {
                var file = root.PackageEntry;
                if (decompile && filesToExtractSorted.TryGetValue(file.TypeName, out var specializedQueue))
                {
                    specializedQueue.Enqueue(file);
                }

                filesToExtract.Enqueue(file);
            }
        }

        private async Task ExtractFilesAsync(Queue<PackageEntry> filesToExtract)
        {
            var initialCount = filesToExtract.Count;
            while (filesToExtract.Count > 0)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                var packageFile = filesToExtract.Dequeue();

                if (extractedFiles.Contains(packageFile.GetFullPath()))
                {
                    continue;
                }

                Invoke(() =>
                {
                    extractProgressBar.Value = 100 - (int)(filesToExtract.Count / (float)initialCount * 100.0f);
                });

                SetProgress($"Extracting {packageFile.GetFullPath()}");

                var stream = AdvancedGuiFileLoader.GetPackageEntryStream(exportData.VrfGuiContext.CurrentPackage, packageFile);
                var outFilePath = Path.Combine(path, packageFile.GetFullPath());
                var outFolder = Path.GetDirectoryName(outFilePath);

                Directory.CreateDirectory(outFolder);

                if (!decompile || !outFilePath.EndsWith("_c", StringComparison.Ordinal))
                {
                    // Extract as is
                    var outStream = File.OpenWrite(outFilePath);
                    await stream.CopyToAsync(outStream).ConfigureAwait(false);
                    outStream.Close();

                    continue;
                }

                using var resource = new Resource
                {
                    FileName = packageFile.GetFullPath(),
                };
                resource.Read(stream);

                if (GltfModelExporter.CanExport(resource))
                {
                    outFilePath = Path.ChangeExtension(outFilePath, "glb");
                }

                await ExtractFile(resource, packageFile.GetFullPath(), outFilePath).ConfigureAwait(false);
            }
        }

        public async Task ExtractFile(Resource resource, string inFilePath, string outFilePath, bool flatSubfiles = false)
        {
            if (GltfModelExporter.CanExport(resource) && Path.GetExtension(outFilePath) is ".glb" or ".gltf")
            {
                gltfExporter.Export(resource, outFilePath, cancellationTokenSource.Token);
                if (gltfExporter.FileLoader is TrackingFileLoader trackingFileLoader)
                {
                    extractedFiles.UnionWith(trackingFileLoader.LoadedFilePaths);
                }

                return;
            }

            var extension = FileExtract.GetExtension(resource);

            if (extension == null)
            {
                outFilePath = outFilePath[..^2]; // remove "_c"
            }
            else
            {
                outFilePath = Path.ChangeExtension(outFilePath, extension);
            }

            ContentFile contentFile = null;
            if (outFilePath.EndsWith(".vmap", StringComparison.Ordinal))
            {
                flatSubfiles = false;
            }

            try
            {
                contentFile = FileExtract.Extract(resource, exportData.VrfGuiContext.FileLoader);

                if (contentFile.Data != null)
                {
                    SetProgress($"+ {outFilePath.Remove(0, path.Length + 1)}");
                    await File.WriteAllBytesAsync(outFilePath, contentFile.Data, cancellationTokenSource.Token).ConfigureAwait(false);
                }

                string contentRelativeFolder;
                foreach (var additionalFile in contentFile.AdditionalFiles)
                {
                    extractedFiles.Add(additionalFile.FileName + "_c");
                    var fileNameOut = additionalFile.FileName;

                    if (additionalFile.Data != null)
                    {
                        if (flatSubfiles)
                        {
                            fileNameOut = Path.GetFileName(fileNameOut);
                        }

                        var outPath = CombineAssetFolder(path, fileNameOut);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath.Full));
                        SetProgress($" + {outPath.Partial}");
                        await File.WriteAllBytesAsync(outPath.Full, additionalFile.Data, cancellationTokenSource.Token).ConfigureAwait(false);
                    }

                    contentRelativeFolder = flatSubfiles ? string.Empty : Path.GetDirectoryName(fileNameOut);

                    await ExtractSubfiles(contentRelativeFolder, additionalFile).ConfigureAwait(false);
                }

                extractedFiles.Add(inFilePath);

                contentRelativeFolder = flatSubfiles ? string.Empty : Path.GetDirectoryName(inFilePath);

                await ExtractSubfiles(contentRelativeFolder, contentFile).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"Failed to extract '{inFilePath}': {e}").ConfigureAwait(false);
                SetProgress($"Failed to extract '{inFilePath}': {e.Message}");
            }
            finally
            {
                contentFile?.Dispose();
            }
        }

        private async Task ExtractSubfiles(string contentRelativeFolder, ContentFile contentFile)
        {
            foreach (var contentSubFile in contentFile.SubFiles)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                contentSubFile.FileName = Path.Combine(contentRelativeFolder, contentSubFile.FileName).Replace(Path.DirectorySeparatorChar, '/');
                var outPath = CombineAssetFolder(path, contentSubFile.FileName);

                if (extractedFiles.Contains(contentSubFile.FileName))
                {
                    SetProgress($"  - {outPath.Partial}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outPath.Full));

                byte[] subFileData;
                try
                {
                    subFileData = contentSubFile.Extract.Invoke();
                }
                catch (Exception e)
                {
                    await Console.Error.WriteLineAsync($"Failed to extract subfile '{contentSubFile.FileName}': {e}").ConfigureAwait(false);
                    SetProgress($"Failed to extract subfile '{contentSubFile.FileName}': {e.Message}");
                    continue;
                }

                if (subFileData.Length > 0)
                {
                    SetProgress($"  + {outPath.Partial}");
                    extractedFiles.Add(contentSubFile.FileName);
                    await File.WriteAllBytesAsync(outPath.Full, subFileData, cancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
        }

        private static (string Full, string Partial) CombineAssetFolder(string userFolder, string assetName)
        {
            var assetFolders = assetName.Split('/')[..^1];
            var userFolders = userFolder.Split(Path.DirectorySeparatorChar);

            var leftChop = 0;

            foreach (var i in Enumerable.Range(0, assetFolders.Length))
            {
                if (Enumerable.SequenceEqual(
                    assetFolders.Reverse().Skip(i),
                    userFolders.Reverse().Take(assetFolders.Length - i)
                ))
                {
                    leftChop = assetFolders.Reverse().Skip(i).Select(x => x.Length + 1).Sum();
                }
            }

            return (Path.Combine(userFolder, assetName[leftChop..]), assetName[leftChop..]);
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        public void SetProgress(string text)
        {
            if (Disposing || IsDisposed || cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            Invoke(() =>
            {
                progressLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
            });
        }
    }
}
