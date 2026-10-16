using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GrimDawnModMerger
{
    class Merger
    {
        // Written into every folder this tool creates. We only ever delete a pre-existing
        // output folder if it carries this marker, so a typo can't wipe a real mod or the game.
        private const string MarkerFileName = ".gdmm_merged";

        public string GAME_DIR { get; set; }

        private CommandLine cmdLine;
        private readonly EventHandler<UILogMessageEventArgs> UILogMessageCallback;

        private string currentMod;
        private string combinedRoot;
        private readonly List<string> rawOverwrites = new List<string>();

        public Merger(EventHandler<UILogMessageEventArgs> UILogMessageCallback)
        {
            this.UILogMessageCallback = UILogMessageCallback;
        }

        public void Closing()
        {
            if (cmdLine != null)
                cmdLine.Kill(gracefully: false);
        }

        private void Log(string message)
        {
            UILogMessageCallback?.Invoke(this, new UILogMessageEventArgs { Message = message });
        }

        /// <returns>true if the merge was started, false if it was refused or failed to start.</returns>
        public bool Merge(string mergeName, List<string> modsToMerge)
        {
            mergeName = (mergeName ?? "").Trim();

            string modsRoot = Path.Combine(GAME_DIR ?? "", "mods");
            if (!Directory.Exists(modsRoot))
            {
                Log("ERROR: no 'mods' folder found in the game directory.");
                return false;
            }
            modsRoot = Path.GetFullPath(modsRoot);

            string error = ValidateMergeName(mergeName, modsToMerge, modsRoot);
            if (error != null)
            {
                Log("ERROR: " + error);
                return false;
            }

            string combinedDir = Path.Combine(modsRoot, mergeName);
            combinedRoot = combinedDir;

            try
            {
                SetupModDirectory(combinedDir);
            }
            catch (Exception e)
            {
                Log("ERROR: " + e.Message);
                return false;
            }

            Log("Merge order (each mod overrides the ones before it): " + string.Join(" -> ", modsToMerge));

            cmdLine = new CommandLine(GAME_DIR, UILogMessageCallback);
            cmdLine.Start();

            try
            {
                rawOverwrites.Clear();
                foreach (string mod in modsToMerge)
                {
                    currentMod = mod;
                    AddMod(Path.Combine(modsRoot, mod), combinedDir);
                }

                if (rawOverwrites.Count > 0)
                {
                    Log("\nWARNING: " + rawOverwrites.Count + " loose file(s) were overwritten by a later mod:");
                    foreach (string s in rawOverwrites.Take(50))
                        Log("  " + s);
                    if (rawOverwrites.Count > 50)
                        Log("  ... and " + (rawOverwrites.Count - 50) + " more");
                }

                // build .arz
                cmdLine.messageQueue.Add(new Message { Command = "BuildDatabase", Args = new string[] { combinedDir, GAME_DIR } });
                cmdLine.Kill(); // graceful: runs everything queued, then closes
                return true;
            }
            catch (Exception e)
            {
                Log("ERROR: merge aborted: " + e.Message);
                cmdLine.Kill(gracefully: false);
                return false;
            }
        }

        private string ValidateMergeName(string mergeName, List<string> modsToMerge, string modsRoot)
        {
            if (mergeName.Length == 0)
                return "Enter a name for the merged mod.";

            if (mergeName.All(c => c == '.'))
                return "'" + mergeName + "' is not a valid merge name.";

            if (mergeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "The merge name contains characters that are not allowed in a folder name.";

            // belt and braces: the result must sit directly inside the mods folder
            string full = Path.GetFullPath(Path.Combine(modsRoot, mergeName));
            string parent = Path.GetDirectoryName(full);
            if (!string.Equals(parent, modsRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return "The merge name must be a plain folder name.";

            if (mergeName.Equals("gdx1", StringComparison.OrdinalIgnoreCase) ||
                mergeName.Equals("gdx2", StringComparison.OrdinalIgnoreCase))
                return "That name is reserved for an official expansion.";

            if (modsToMerge.Any(m => m.Equals(mergeName, StringComparison.OrdinalIgnoreCase)))
                return "The merge name is the same as one of the mods being merged. Pick a different name.";

            return null;
        }

        private void AddMod(string modDir, string combinedDir)
        {
            // raw copy any resources
            DirectoryCopy(modDir, combinedDir, true);

            // decompile any .arc or .arz into their respective folders
            string resourcesDir = Path.Combine(modDir, "resources");
            if (Directory.Exists(resourcesDir))
            {
                foreach (FileInfo file in new DirectoryInfo(resourcesDir).GetFiles())
                {
                    if (IsExtension(file, ".arc"))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(file.Name);
                        ExtractArc(file, Path.Combine(combinedDir, "source"), cmdLine);

                        cmdLine.messageQueue.Add(new Message
                        {
                            Command = "Pack",
                            Args = new string[]
                            {
                                Path.Combine(combinedDir, "source", baseName),
                                baseName,
                                Path.Combine(combinedDir, "resources", file.Name)
                            }
                        });
                    }
                }
            }
            else
            {
                Log("Note: " + currentMod + " has no 'resources' folder, skipping its resource archives.");
            }

            string databaseDir = Path.Combine(modDir, "database");
            if (Directory.Exists(databaseDir))
            {
                foreach (FileInfo file in new DirectoryInfo(databaseDir).GetFiles())
                {
                    if (IsExtension(file, ".arz"))
                        ExtractArz(file, Path.Combine(combinedDir, "database"), cmdLine);
                    else if (IsExtension(file, ".arc"))
                        ExtractArc(file, Path.Combine(combinedDir, "database"), cmdLine);
                }
            }
            else
            {
                Log("Note: " + currentMod + " has no 'database' folder, skipping its database archives.");
            }
        }

        private static bool IsExtension(FileInfo file, string extension)
        {
            return file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        private void ExtractArc(FileInfo file, string destination, CommandLine cmdLine) // TODO move this out of merger
        {
            Directory.CreateDirectory(destination);
            cmdLine.messageQueue.Add(new Message { Command = "Extract", Args = new string[] { file.FullName, destination } });
        }

        private void ExtractArz(FileInfo file, string destination, CommandLine cmdLine) // TODO move this out of merger
        {
            Directory.CreateDirectory(destination);
            cmdLine.messageQueue.Add(new Message { Command = "ExtractDatabase", Args = new string[] { file.FullName, destination } });
        }

        private void SetupModDirectory(string combinedDir)
        {
            if (Directory.Exists(combinedDir))
            {
                if ((File.GetAttributes(combinedDir) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("'" + combinedDir + "' is a link/junction, refusing to delete it.");

                if (!File.Exists(Path.Combine(combinedDir, MarkerFileName)))
                    throw new InvalidOperationException(
                        "The folder '" + combinedDir + "' already exists and was not created by this tool, so it will not be deleted. " +
                        "Choose a different merge name, or delete that folder yourself if you are sure it is an old merge.");

                Directory.Delete(combinedDir, true);
            }

            Directory.CreateDirectory(combinedDir);
            Directory.CreateDirectory(Path.Combine(combinedDir, "source"));
            Directory.CreateDirectory(Path.Combine(combinedDir, "database", "templates"));
            File.WriteAllText(Path.Combine(combinedDir, MarkerFileName), "Created by Grim Dawn Mod Merger. Safe to delete/overwrite.");
        }

        private void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs) // TODO move this out of merger
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();

            Directory.CreateDirectory(destDirName);

            foreach (FileInfo file in dir.GetFiles())
            {
                if (IsExtension(file, ".arz") || IsExtension(file, ".arc"))
                    continue;

                string tempPath = Path.Combine(destDirName, file.Name);
                if (File.Exists(tempPath))
                {
                    string relative = tempPath.StartsWith(combinedRoot, StringComparison.OrdinalIgnoreCase)
                        ? tempPath.Substring(combinedRoot.Length).TrimStart(Path.DirectorySeparatorChar)
                        : tempPath;
                    rawOverwrites.Add(currentMod + " overwrote " + relative);
                }
                file.CopyTo(tempPath, true);
            }

            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string tempPath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
                }
            }
        }
    }
}
