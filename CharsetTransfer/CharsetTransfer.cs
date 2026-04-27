using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CharsetTransfer
{
    /// <summary>
    /// Transfers Charset table from a source language file to a target language file.
    /// This is useful for copying character sets from vanilla game files to modded game files.
    /// </summary>
    public class CharsetTransferer
    {
        /// <summary>
        /// Transfer Charset from source file to target file
        /// </summary>
        /// <param name="sourceFilePath">Path to the source file (vanilla language file with full charset)</param>
        /// <param name="targetFilePath">Path to the target file (modded game file to be updated)</param>
        /// <param name="outputFilePath">Path where the modified target file will be saved</param>
        /// <returns>True if transfer was successful</returns>
        public bool TransferCharset(string sourceFilePath, string targetFilePath, string outputFilePath)
        {
            try
            {
                // Validate files exist
                if (!File.Exists(sourceFilePath))
                {
                    Console.WriteLine($"ERROR: Source file not found: {sourceFilePath}");
                    return false;
                }

                if (!File.Exists(targetFilePath))
                {
                    Console.WriteLine($"ERROR: Target file not found: {targetFilePath}");
                    return false;
                }

                Console.WriteLine($"Reading source file: {Path.GetFileName(sourceFilePath)}");
                var sourceFile = new Labrune.File(sourceFilePath);
                sourceFile.ReadChunks();

                Console.WriteLine($"Reading target file: {Path.GetFileName(targetFilePath)}");
                var targetFile = new Labrune.File(targetFilePath);
                targetFile.ReadChunks();

                // Find LanguageHistogramChunk (contains Charset) in source file
                var sourceHistogramChunk = FindLanguageHistogramChunk(sourceFile.Chunks);
                if (sourceHistogramChunk == null)
                {
                    Console.WriteLine("ERROR: Source file does not contain a LanguageHistogramChunk (no Charset table found)");
                    return false;
                }

                Console.WriteLine($"Source file Charset found with {sourceHistogramChunk.CharacterSet.NumberOfEntries} entries");

                // Find LanguageHistogramChunk in target file
                var targetHistogramChunk = FindLanguageHistogramChunk(targetFile.Chunks);
                if (targetHistogramChunk == null)
                {
                    Console.WriteLine("WARNING: Target file does not contain a LanguageHistogramChunk. This file may not support embedded Charsets.");
                    Console.WriteLine("The target file must be in 'Old' format (MW/U/U2 with embedded Charset) to receive the charset transfer.");
                    return false;
                }

                // Transfer the Charset
                Console.WriteLine("Transferring Charset from source to target...");
                targetHistogramChunk.CharacterSet = sourceHistogramChunk.CharacterSet;

                Console.WriteLine($"Target file Charset updated to {targetHistogramChunk.CharacterSet.NumberOfEntries} entries");

                // Write the modified target file
                Console.WriteLine($"Writing modified file: {Path.GetFileName(outputFilePath)}");
                targetFile.FileName = outputFilePath;
                targetFile.WriteChunks();

                Console.WriteLine($"SUCCESS: Charset transferred successfully!");
                Console.WriteLine($"Output file: {outputFilePath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Transfer Charset from source files in a directory to corresponding target files in another directory
        /// </summary>
        public bool TransferCharsetBatch(string sourceDirectory, string targetDirectory, string outputDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory))
                {
                    Console.WriteLine($"ERROR: Source directory not found: {sourceDirectory}");
                    return false;
                }

                if (!Directory.Exists(targetDirectory))
                {
                    Console.WriteLine($"ERROR: Target directory not found: {targetDirectory}");
                    return false;
                }

                // Create output directory if it doesn't exist
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var sourceFiles = Directory.GetFiles(sourceDirectory, "*.bin");
                Console.WriteLine($"Found {sourceFiles.Length} source .bin files");

                // First pass: Find a source file with a Charset (we'll use this as fallback for others)
                Labrune.LanguageHistogramChunk fallbackCharset = null;
                foreach (var sourceFile in sourceFiles)
                {
                    var f = new Labrune.File(sourceFile);
                    f.ReadChunks();
                    var hist = FindLanguageHistogramChunk(f.Chunks);
                    if (hist != null)
                    {
                        fallbackCharset = hist;
                        Console.WriteLine($"Found Charset in {Path.GetFileName(sourceFile)} - will use as fallback");
                        break;
                    }
                }

                int successCount = 0;
                int fallbackCount = 0;

                // Second pass: Transfer Charsets
                foreach (var sourceFile in sourceFiles)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    
                    // Try to find matching target file (case-insensitive, remove language prefix)
                    var targetFile = Directory.GetFiles(targetDirectory, "*.bin")
                        .FirstOrDefault(f => Path.GetFileName(f).ToLower().EndsWith(
                            Path.GetFileNameWithoutExtension(fileName).Split('_').Last().ToLower() + ".bin"));

                    if (targetFile == null)
                    {
                        Console.WriteLine($"WARNING: No matching target file found for {fileName}");
                        continue;
                    }

                    var outputFile = Path.Combine(outputDirectory, Path.GetFileName(targetFile));
                    
                    // Copy target file to output first
                    File.Copy(targetFile, outputFile, true);

                    Console.WriteLine($"\n--- Processing: {fileName} ---");
                    
                    // Try to use the source file's own Charset
                    if (TransferCharset(sourceFile, outputFile, outputFile))
                    {
                        successCount++;
                    }
                    // If source doesn't have Charset, try using fallback
                    else if (fallbackCharset != null)
                    {
                        Console.WriteLine($"Source file has no Charset, applying fallback from global file...");
                        if (ApplyCharsetToFile(outputFile, fallbackCharset))
                        {
                            fallbackCount++;
                            successCount++;
                        }
                    }
                }

                Console.WriteLine($"\n=== BATCH TRANSFER COMPLETE ===");
                Console.WriteLine($"Successfully transferred (own charset): {successCount - fallbackCount}/{sourceFiles.Length} files");
                Console.WriteLine($"Successfully transferred (fallback charset): {fallbackCount}/{sourceFiles.Length} files");
                return successCount == sourceFiles.Length;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Apply a Charset to a file (even if it doesn't have one originally)
        /// </summary>
        private bool ApplyCharsetToFile(string filePath, Labrune.LanguageHistogramChunk charsetToApply)
        {
            try
            {
                var targetFile = new Labrune.File(filePath);
                targetFile.ReadChunks();

                var targetHistogramChunk = FindLanguageHistogramChunk(targetFile.Chunks);
                if (targetHistogramChunk == null)
                {
                    // File doesn't have a Charset chunk - can't apply
                    Console.WriteLine("WARNING: Target file does not contain a LanguageHistogramChunk.");
                    return false;
                }

                // Transfer the Charset
                Console.WriteLine("Transferring fallback Charset...");
                targetHistogramChunk.CharacterSet = charsetToApply.CharacterSet;

                Console.WriteLine($"Target file Charset updated to {targetHistogramChunk.CharacterSet.NumberOfEntries} entries");

                // Write the modified target file
                Console.WriteLine($"Writing modified file: {Path.GetFileName(filePath)}");
                targetFile.FileName = filePath;
                targetFile.WriteChunks();

                Console.WriteLine($"SUCCESS: Fallback Charset applied!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR applying charset: {ex.Message}");
                return false;
            }
        }

        private Labrune.LanguageHistogramChunk FindLanguageHistogramChunk(List<Labrune.Chunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                if (chunk.ID == (uint)Labrune.ChunkID.BCHUNK_LANGUAGEHISTOGRAM)
                {
                    return new Labrune.LanguageHistogramChunk(chunk);
                }
            }
            return null;
        }
    }

    // Program entry point
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string mode = args[0].ToLower();

            if (mode == "single" && args.Length >= 3)
            {
                // Single file transfer: transfer-charset.exe single <source> <target> [output]
                string sourceFile = args[1];
                string targetFile = args[2];
                string outputFile = args.Length > 3 ? args[3] : targetFile;

                Console.WriteLine("=== Charset Transfer Tool ===\n");
                var transferer = new CharsetTransferer();
                transferer.TransferCharset(sourceFile, targetFile, outputFile);
            }
            else if (mode == "batch" && args.Length >= 3)
            {
                // Batch transfer: transfer-charset.exe batch <sourceDir> <targetDir> [outputDir]
                string sourceDir = args[1];
                string targetDir = args[2];
                string outputDir = args.Length > 3 ? args[3] : targetDir;

                Console.WriteLine("=== Charset Transfer Tool (Batch Mode) ===\n");
                var transferer = new CharsetTransferer();
                transferer.TransferCharsetBatch(sourceDir, targetDir, outputDir);
            }
            else
            {
                PrintUsage();
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("=== Charset Transfer Tool ===");
            Console.WriteLine("Transfers Charset tables from vanilla game files to modded game files\n");
            Console.WriteLine("USAGE:");
            Console.WriteLine("  Single file:  CharsetTransfer.exe single <sourceFile> <targetFile> [outputFile]");
            Console.WriteLine("  Batch mode:   CharsetTransfer.exe batch <sourceDir> <targetDir> [outputDir]\n");
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("  CharsetTransfer.exe single russian_vanilla.bin english_modded.bin english_with_russian_charset.bin");
            Console.WriteLine("  CharsetTransfer.exe batch C:\\db\\RU\\Vanilla C:\\db\\RU\\Modded C:\\db\\RU\\Output");
        }
    }
}
