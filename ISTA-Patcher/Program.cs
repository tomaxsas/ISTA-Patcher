﻿// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2022-2023 TautCony

namespace ISTA_Patcher;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CommandLine;
using ISTA_Patcher.LicenseManagement;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using AssemblyDefinition = dnlib.DotNet.AssemblyDef;
using DecryptOptions = ProgramArgs.DecryptOptions;
using LicenseOptions = ProgramArgs.LicenseOptions;
using PatchOptions = ProgramArgs.PatchOptions;
using PatchTypeEnum = ProgramArgs.PatchTypeEnum;

internal static class ISTAPatcher
{
    public static int Main(string[] args)
    {
        var levelSwitch = new LoggingLevelSwitch
        {
            MinimumLevel = LogEventLevel.Information,
        };
        Log.Logger = new LoggerConfiguration()
                     .MinimumLevel.ControlledBy(levelSwitch)
                     .WriteTo.Console()
                     .CreateLogger();

        return Parser.Default.ParseArguments<PatchOptions, DecryptOptions, LicenseOptions>(args)
                     .MapResult(
                         (PatchOptions opts) =>
                         {
                             levelSwitch.MinimumLevel = opts.Verbosity;
                             return RunPatchAndReturnExitCode(opts);
                         },
                         (DecryptOptions opts) =>
                         {
                             levelSwitch.MinimumLevel = opts.Verbosity;
                             return RunDecryptAndReturnExitCode(opts);
                         },
                         (LicenseOptions opts) =>
                         {
                             levelSwitch.MinimumLevel = opts.Verbosity;
                             return RunLicenseOperationAndReturnExitCode(opts);
                         },
                         errs => 1);

        static int RunPatchAndReturnExitCode(PatchOptions opts)
        {
            var guiBasePath = Path.Join(opts.TargetPath, "TesterGUI", "bin", "Release");
            var psdzBasePath = Path.Join(opts.TargetPath, "PSdZ", "host");

            if (!Directory.Exists(guiBasePath) || !Directory.Exists(psdzBasePath))
            {
                Log.Fatal("Folder structure does not match, please check options");
                return -1;
            }

            IPatcher patcher = opts.PatchType switch
            {
                PatchTypeEnum.BMW => new BMWPatcher(opts),
                PatchTypeEnum.TOYOTA => new ToyotaPatcher(),
                _ => throw new NotImplementedException(),
            };

            PatchISTA(patcher, opts);
            return 0;
        }

        static int RunDecryptAndReturnExitCode(DecryptOptions opts)
        {
            var encryptedFileList = Path.Join(opts.TargetPath, "Ecu", "enc_cne_1.prg");
            if (!File.Exists(encryptedFileList))
            {
                Log.Error("File {FilePath} does not exist", encryptedFileList);
                return -1;
            }

            var fileList = IntegrityManager.DecryptFile(encryptedFileList);
            if (fileList == null)
            {
                return -1;
            }

            var filePathMaxLength = fileList.Select(f => f.FilePath.Length).Max();
            var hashMaxLength = fileList.Select(f => f.Hash.Length).Max();
            var markdownBuilder = new StringBuilder();
            markdownBuilder.AppendLine($"| {"FilePath".PadRight(filePathMaxLength)} | {"Hash(SHA256)".PadRight(hashMaxLength)} |");
            markdownBuilder.AppendLine($"| {"---".PadRight(filePathMaxLength)} | {"---".PadRight(hashMaxLength)} |");
            foreach (var fileInfo in fileList)
            {
                markdownBuilder.AppendLine($"| {fileInfo.FilePath.PadRight(filePathMaxLength)} | {fileInfo.Hash.PadRight(hashMaxLength)} |");
            }

            Log.Information("Markdown result:\n{Markdown}", markdownBuilder.ToString());
            return 0;
        }

        static int RunLicenseOperationAndReturnExitCode(LicenseOptions opts)
        {
            var privateKeyPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "privateKey.xml");
            if (opts.AutoMode)
            {
                if (opts.TargetPath == null || opts.LicenseRequestPath == null)
                {
                    Log.Fatal("You must specify --patch and --license options");
                    return -1;
                }

                opts.GenerateKeyPair = true;
                opts.KeyPairPath = privateKeyPath;
                opts.SignLicense = true;
            }

            string keyPairXml = null;
            if (opts.KeyPairPath != null)
            {
                if (!File.Exists(opts.KeyPairPath))
                {
                    Log.Error("Private key pair {KeyPairPath} does not exist", opts.KeyPairPath);
                    return -1;
                }

                using var fs = File.OpenRead(opts.KeyPairPath);
                using var sr = new StreamReader(fs, new UTF8Encoding(false));
                keyPairXml = sr.ReadToEnd();
                Log.Debug("Loaded private key from {KeyPairPath}", opts.KeyPairPath);
            }

            string licenseXml = null;
            if (opts.LicenseRequestPath != null)
            {
                if (opts.Base64)
                {
                    try
                    {
                        var data = Convert.FromBase64String(opts.LicenseRequestPath);
                        licenseXml = Encoding.UTF8.GetString(data);
                        Log.Debug("Loaded license request from parameter");
                    }
                    catch (FormatException ex)
                    {
                        Log.Error(ex, "License request is not a valid base64 string");
                        return -1;
                    }
                }
                else
                {
                    if (!File.Exists(opts.LicenseRequestPath))
                    {
                        Log.Error("License request file {LicensePath} does not exist", opts.LicenseRequestPath);
                        return -1;
                    }

                    using var fs = File.OpenRead(opts.LicenseRequestPath);
                    using var sr = new StreamReader(fs, new UTF8Encoding(false));
                    licenseXml = sr.ReadToEnd();
                    Log.Debug("Loaded license request from {LicensePath}", opts.LicenseRequestPath);
                }
            }

            // --generate
            if (opts.GenerateKeyPair)
            {
                // Generate key pair
                using var rsa = new RSACryptoServiceProvider(2048);
                try
                {
                    var privateKey = rsa.ToXmlString(true);

                    using var fs = new FileStream(privateKeyPath, FileMode.Create);
                    using var sw = new StreamWriter(fs);
                    sw.Write(privateKey);
                    Log.Information("Generated key pair located at {PrivateKeyPath}", privateKeyPath);
                }
                finally
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.Clear();
                }

                if (!opts.AutoMode)
                {
                    return 0;
                }
            }

            // --sign
            if (opts.SignLicense && keyPairXml != null && licenseXml != null)
            {
                var license = LicenseInfoSerializer.DeserializeFromString(licenseXml);
                if (license == null)
                {
                    Log.Error("License request is not valid");
                    return -1;
                }

                var isValid = false;
                if (license.LicenseKey is { Length: > 0 })
                {
                    // verify license
                    var deformatter = LicenseStatusChecker.GetRSAPKCS1SignatureDeformatter(keyPairXml);
                    isValid = LicenseStatusChecker.IsLicenseValid(license, deformatter);
                    Log.Information("License is valid: {IsValid}", isValid);
                }

                if (isValid)
                {
                    Log.Debug("License is valid, no need to patch");
                    return 0;
                }

                // update license info
                license.Comment = "Patched by ISTA Patcher (https://github.com/tautcony/ISTA-Patcher)";
                license.Expiration = DateTime.MaxValue;
                foreach (var subLicense in license.SubLicenses)
                {
                    subLicense.PackageRule ??= "true";
                    subLicense.PackageExpire = DateTime.MaxValue;
                }

                // generate license key
                LicenseStatusChecker.GenerateLicenseKey(license, keyPairXml);
                var signedLicense = LicenseInfoSerializer.SerializeLicenseToByteArray(license);
                if (opts.SignedLicensePath != null)
                {
                    using var fileStream = File.Create(opts.SignedLicensePath);
                    fileStream.Write(signedLicense);
                }
                else
                {
                    Log.Information("License:\n{License}", Convert.ToBase64String(signedLicense));
                }

                if (!opts.AutoMode)
                {
                    return 0;
                }
            }

            // --patch
            if (keyPairXml != null && opts.TargetPath != null)
            {
                if (!Directory.Exists(opts.TargetPath))
                {
                    Log.Error("Target directory {TargetPath} does not exist", opts.TargetPath);
                    return -1;
                }

                // Patch program
                var rsaCryptoServiceProvider = new RSACryptoServiceProvider();
                rsaCryptoServiceProvider.FromXmlString(keyPairXml);

                var parameters = rsaCryptoServiceProvider.ExportParameters(true);

                var modulus = Convert.ToBase64String(parameters.Modulus);
                var exponent = Convert.ToBase64String(parameters.Exponent);

                PatchISTA(new BMWLicensePatcher(modulus, exponent), new PatchOptions
                {
                    TargetPath = opts.TargetPath,
                    Force = opts.Force,
                    Deobfuscate = opts.Deobfuscate,
                });

                return 0;
            }

            Log.Warning("No operation matched, exiting...");
            return 1;
        }
    }

    private static IEnumerable<string> BuildIndicator(IReadOnlyCollection<Func<AssemblyDefinition, int>> patches)
    {
        return patches
               .Select(i => i.Method.Name.StartsWith("Patch") ? i.Method.Name[5..] : i.Method.Name)
               .Reverse()
               .ToList()
               .Select((name, idx) => $"{new string('│', patches.Count - 1 - idx)}└{new string('─', idx)}>[{name}]");
    }

    private static void PatchISTA(IPatcher patcher, PatchOptions options, string outputDirName = "@ista-patched", string bakDirName = "@ista-backup")
    {
        var guiBasePath = Path.Join(options.TargetPath, "TesterGUI", "bin", "Release");

        var validPatches = patcher.Patches;
        var pendingPatchList = patcher.GeneratePatchList(options.TargetPath);

        Log.Information("=== ISTA Patch Begin ===");
        var timer = Stopwatch.StartNew();
        var indentLength = pendingPatchList.Select(i => i.Length).Max() + 1;

        foreach (var pendingPatchItem in pendingPatchList)
        {
            var pendingPatchItemFullPath = pendingPatchItem.StartsWith("!") ? Path.Join(options.TargetPath, pendingPatchItem.Trim('!')) : Path.Join(guiBasePath, pendingPatchItem);

            var originalDirPath = Path.GetDirectoryName(pendingPatchItemFullPath);
            var patchedDirPath = Path.Join(originalDirPath, outputDirName);
            var patchedFileFullPath = Path.Join(patchedDirPath, Path.GetFileName(pendingPatchItem));
            var bakDirPath = Path.Join(originalDirPath, bakDirName);
            var bakFileFullPath = Path.Join(bakDirPath, Path.GetFileName(pendingPatchItem));

            if (File.Exists(patchedFileFullPath))
            {
                File.Delete(patchedFileFullPath);
            }

            var indent = new string(' ', indentLength - pendingPatchItem.Length);
            if (!File.Exists(pendingPatchItemFullPath))
            {
                Log.Information(
                    "{Item}{Indent}{Result} [not found]",
                    pendingPatchItem,
                    indent,
                    string.Concat(Enumerable.Repeat("*", validPatches.Length)));
                continue;
            }

            Directory.CreateDirectory(patchedDirPath);
            Directory.CreateDirectory(bakDirPath);

            try
            {
                var module = PatchUtils.LoadModule(pendingPatchItemFullPath);
                var assembly = module.Assembly;
                var isPatched = PatchUtils.HavePatchedMark(assembly);
                if (isPatched && !options.Force)
                {
                    Log.Information(
                        "{Item}{Indent}{Result} [already patched]",
                        pendingPatchItem,
                        indent,
                        string.Concat(Enumerable.Repeat("*", validPatches.Length)));
                    continue;
                }

                // Patch and print result
                var result = validPatches.Select(patch => patch(assembly)).ToList();
                isPatched = result.Any(i => i > 0);
                var resultStr = result.Aggregate(string.Empty, (c, i) => c + (i > 0 ? i.ToString("X") : "-"));

                // Check if patched
                if (!isPatched)
                {
                    Log.Information("{Item}{Indent}{Result} [skip]", pendingPatchItem, indent, resultStr);
                    continue;
                }

                if (!File.Exists(bakFileFullPath))
                {
                    File.Copy(pendingPatchItemFullPath, bakFileFullPath, false);
                    continue;
                }

                PatchUtils.SetPatchedMark(assembly);
                assembly.Write(patchedFileFullPath);

                // Check if need to deobfuscate
                if (!options.Deobfuscate)
                {
                    var patchedFunctionCount = result.Aggregate(0, (c, i) => c + i);
                    Log.Information("{Item}{Indent}{Result} [{PatchedFunctionCount} func patched]", pendingPatchItem, indent, resultStr, patchedFunctionCount);
                    continue;
                }

                try
                {
                    var deobfTimer = Stopwatch.StartNew();

                    var deobfPath = patchedFileFullPath + ".deobf";
                    PatchUtils.DeObfuscation(patchedFileFullPath, deobfPath);
                    if (File.Exists(patchedFileFullPath))
                    {
                        File.Delete(patchedFileFullPath);
                    }

                    File.Move(deobfPath, patchedFileFullPath);

                    deobfTimer.Stop();
                    var timeStr = deobfTimer.ElapsedTicks > Stopwatch.Frequency
                        ? $" in {deobfTimer.Elapsed:mm\\:ss}"
                        : string.Empty;
                    Log.Information(
                        "{Item}{Indent}{Result} [patched][deobfuscate success{Time}]",
                        pendingPatchItem,
                        indent,
                        resultStr,
                        timeStr);
                }
                catch (ApplicationException ex)
                {
                    Log.Information(
                        "{Item}{Indent}{Result} [patched][deobfuscate skipped]: {Reason}",
                        pendingPatchItem,
                        indent,
                        resultStr,
                        ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Information(
                    "{Item}{Indent}{Result} [failed]: {Reason}",
                    pendingPatchItem,
                    indent,
                    string.Concat(Enumerable.Repeat("*", validPatches.Length)),
                    ex.Message);

                if (File.Exists(patchedFileFullPath))
                {
                    File.Delete(patchedFileFullPath);
                }
            }
        }

        foreach (var line in BuildIndicator(validPatches))
        {
            Log.Information("{Indent}{Line}", new string(' ', indentLength), line);
        }

        timer.Stop();
        Log.Information("=== ISTA Patch Done in {Time:mm\\:ss} ===", timer.Elapsed);
    }
}
