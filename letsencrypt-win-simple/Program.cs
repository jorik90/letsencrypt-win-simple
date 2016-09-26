﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Security.Principal;
using CommandLine;
using Microsoft.Win32.TaskScheduler;
using System.Reflection;
using ACMESharp;
using ACMESharp.HTTP;
using ACMESharp.JOSE;
using ACMESharp.PKI;
using System.Security.Cryptography;
using ACMESharp.ACME;
using Serilog;
using System.Text;

namespace LetsEncrypt.ACME.Simple
{
    class Program
    {
        private const string ClientName = "letsencrypt-win-simple";
        private static string _certificateStore = "WebHosting";
        public static float RenewalPeriod = 60;
        public static bool CentralSsl = false;
        public static string BaseUri { get; set; }
        private static string _configPath;
        private static string _certificatePath;
        private static Settings _settings;
        private static AcmeClient _client;
        public static Options Options;

        static bool IsElevated
            => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        private static void Main(string[] args)
        {
            CreateLogger();

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            if (!TryParseOptions(args))
                return;

            Console.WriteLine("Let's Encrypt (Simple Windows ACME Client)");
            BaseUri = Options.BaseUri;

            if (Options.Test)
                SetTestParameters();

            if (Options.San)
                Log.Debug("San Option Enabled: Running per site and not per host");

            ParseRenewalPeriod();
            ParseCertificateStore();

            Console.WriteLine($"\nACME Server: {BaseUri}");
            Log.Information("ACME Server: {BaseUri}", BaseUri);

            ParseCentralSslStore();

            CreateSettings();
            CreateConfigPath();

            SetAndCreateCertificatePath();

            try
            {
                using (var signer = new RS256Signer())
                {
                    signer.Init();

                    var signerPath = Path.Combine(_configPath, "Signer");
                    if (File.Exists(signerPath))
                        LoadSignerFromFile(signer, signerPath);

                    using (_client = new AcmeClient(new Uri(BaseUri), new AcmeServerDirectory(), signer))
                    {
                        _client.Init();
                        Console.WriteLine("\nGetting AcmeServerDirectory");
                        Log.Information("Getting AcmeServerDirectory");
                        _client.GetDirectory(true);

                        var registrationPath = Path.Combine(_configPath, "Registration");
                        if (File.Exists(registrationPath))
                            LoadRegistrationFromFile(registrationPath);
                        else
                        {
                            Console.Write("Enter an email address (not public, used for renewal fail notices): ");
                            var email = Console.ReadLine().Trim();

                            string[] contacts = GetContacts(email);

                            AcmeRegistration registration = CreateRegistration(contacts);

                            if (!Options.AcceptTos && !Options.Renew)
                            {
                                Console.WriteLine($"Do you agree to {registration.TosLinkUri}? (Y/N) ");
                                if (!PromptYesNo())
                                    return;
                            }

                            UpdateRegistration();
                            SaveRegistrationToFile(registrationPath);
                            SaveSignerToFile(signer, signerPath);
                        }

                        if (Options.Renew)
                        {
                            CheckRenewalsAndWaitForEnterKey();
                            return;
                        }

                        List<Target> targets = GetTargetsSorted();

                        WriteBindings(targets);

                        Console.WriteLine();
                        PrintMenuForPlugins();

                        if (string.IsNullOrEmpty(Options.ManualHost))
                        {
                            Console.WriteLine(" A: Get certificates for all hosts");
                            Console.WriteLine(" Q: Quit");
                            Console.Write("Which host do you want to get a certificate for: ");
                            var command = ReadCommandFromConsole();
                            switch (command)
                            {
                                case "a":
                                    GetCertificatesForAllHosts(targets);
                                    break;
                                case "q":
                                    return;
                                default:
                                    ProcessDefaultCommand(targets, command);
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Error {@e}", e);
                Console.ForegroundColor = ConsoleColor.Red;
                var acmeWebException = e as AcmeClient.AcmeWebException;
                if (acmeWebException != null)
                {
                    Console.WriteLine(acmeWebException.Message);
                    Console.WriteLine("ACME Server Returned:");
                    Console.WriteLine(acmeWebException.Response.ContentAsString);
                }
                else
                {
                    Console.WriteLine(e);
                }
                Console.ResetColor();
            }

            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
        }

        private static bool TryParseOptions(string[] args)
        {
            try
            {
                var commandLineParseResult = Parser.Default.ParseArguments<Options>(args);
                var parsed = commandLineParseResult as Parsed<Options>;
                if (parsed == null)
                {
                    LogParsingErrorAndWaitForEnter();
                    return false; // not parsed
                }

                Options = parsed.Value;
                Log.Debug("{@Options}", Options);

                return true;
            }
            catch
            {
                Console.WriteLine("Failed while parsing options.");
                throw;
            }
        }

        private static AcmeRegistration CreateRegistration(string[] contacts)
        {
            Console.WriteLine("Calling Register");
            Log.Information("Calling Register");
            var registration = _client.Register(contacts);
            return registration;
        }

        private static void SetTestParameters()
        {
            BaseUri = "https://acme-staging.api.letsencrypt.org/";
            Log.Debug("Test paramater set: {BaseUri}", BaseUri);
        }

        private static void ProcessDefaultCommand(List<Target> targets, string command)
        {
            var targetId = 0;
            if (Int32.TryParse(command, out targetId))
            {
                GetCertificateForTargetId(targets, targetId);
                return;
            }

            HandleMenuResponseForPlugins(targets, command);
        }

        private static void HandleMenuResponseForPlugins(List<Target> targets, string command)
        {
            foreach (var plugin in Target.Plugins.Values)
                plugin.HandleMenuResponse(command, targets);
        }

        private static void GetCertificateForTargetId(List<Target> targets, int targetId)
        {
            if (!Options.San)
            {
                var targetIndex = targetId - 1;
                if (targetIndex >= 0 && targetIndex < targets.Count)
                {
                    Target binding = GetBindingByIndex(targets, targetIndex);
                    binding.Plugin.Auto(binding);
                }
            }
            else
            {
                Target binding = GetBindingBySiteId(targets, targetId);
                binding.Plugin.Auto(binding);
            }
        }

        private static Target GetBindingByIndex(List<Target> targets, int targetIndex)
        {
            return targets[targetIndex];
        }

        private static Target GetBindingBySiteId(List<Target> targets, int targetId)
        {
            return targets.Where(t => t.SiteId == targetId).First();
        }

        private static void GetCertificatesForAllHosts(List<Target> targets)
        {
            foreach (var target in targets)
                target.Plugin.Auto(target);
        }

        private static void CheckRenewalsAndWaitForEnterKey()
        {
            CheckRenewals();
            WaitForEnterKey();
        }

        private static void WaitForEnterKey()
        {
#if DEBUG
            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
#endif
        }

        private static void LoadRegistrationFromFile(string registrationPath)
        {
            Console.WriteLine($"Loading Registration from {registrationPath}");
            Log.Information("Loading Registration from {registrationPath}", registrationPath);
            using (var registrationStream = File.OpenRead(registrationPath))
                _client.Registration = AcmeRegistration.Load(registrationStream);
        }

        private static string[] GetContacts(string email)
        {
            var contacts = new string[] { };
            if (!String.IsNullOrEmpty(email))
            {
                Log.Debug("Registration email: {email}", email);
                email = "mailto:" + email;
                contacts = new string[] { email };
            }

            return contacts;
        }

        private static void SaveSignerToFile(RS256Signer signer, string signerPath)
        {
            Console.WriteLine("Saving Signer");
            Log.Information("Saving Signer");
            using (var signerStream = File.OpenWrite(signerPath))
                signer.Save(signerStream);
        }

        private static void SaveRegistrationToFile(string registrationPath)
        {
            Console.WriteLine("Saving Registration");
            Log.Information("Saving Registration");
            using (var registrationStream = File.OpenWrite(registrationPath))
                _client.Registration.Save(registrationStream);
        }

        private static void UpdateRegistration()
        {
            Console.WriteLine("Updating Registration");
            Log.Information("Updating Registration");
            _client.UpdateRegistration(true, true);
        }

        private static void WriteBindings(List<Target> targets)
        {
            if (targets.Count == 0 && string.IsNullOrEmpty(Options.ManualHost))
                WriteNoTargetsFound();
            else
            {
                int hostsPerPage = GetHostsPerPageFromSettings();

                if (targets.Count > hostsPerPage)
                    WriteBindingsFromTargetsPaged(targets, hostsPerPage, 1);
                else
                    WriteBindingsFromTargetsPaged(targets, targets.Count, 1);
            }
        }

        private static void PrintMenuForPlugins()
        {
            foreach (var plugin in Target.Plugins.Values)
            {
                if (string.IsNullOrEmpty(Options.ManualHost))
                {
                    plugin.PrintMenu();
                }
                else if (plugin.Name == "Manual")
                {
                    plugin.PrintMenu();
                }
            }
        }

        private static int GetHostsPerPageFromSettings()
        {
            int hostsPerPage = 50;
            try
            {
                hostsPerPage = Properties.Settings.Default.HostsPerPage;
            }
            catch (Exception ex)
            {
                Log.Error("Error getting HostsPerPage setting, setting to default value. Error: {@ex}",
                    ex);
            }

            return hostsPerPage;
        }

        private static void WriteNoTargetsFound()
        {
            Console.WriteLine("No targets found.");
            Log.Error("No targets found.");
        }

        private static void LoadSignerFromFile(RS256Signer signer, string signerPath)
        {
            Console.WriteLine($"Loading Signer from {signerPath}");
            Log.Information("Loading Signer from {signerPath}", signerPath);
            using (var signerStream = File.OpenRead(signerPath))
                signer.Load(signerStream);
        }

        private static void SetAndCreateCertificatePath()
        {
            _certificatePath = Properties.Settings.Default.CertificatePath;

            if (string.IsNullOrWhiteSpace(_certificatePath))
                _certificatePath = _configPath;
            else
                CreateCertificatePath();

            Console.WriteLine("Certificate Folder: " + _certificatePath);
            Log.Information("Certificate Folder: {_certificatePath}", _certificatePath);

        }

        private static void CreateCertificatePath()
        {
            try
            {
                Directory.CreateDirectory(_certificatePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Error creating the certificate directory, {_certificatePath}. Defaulting to config path");
                Log.Warning(
                    "Error creating the certificate directory, {_certificatePath}. Defaulting to config path. Error: {@ex}",
                    _certificatePath, ex);

                _certificatePath = _configPath;
            }
        }

        private static void CreateConfigPath()
        {
            // Environment.SpecialFolder.ApplicationData does not return the path for the correct user when executing as a task
            // in some occassions. There is a hotfix at https://support.microsoft.com/en-us/kb/3133689, but it isn't always installed.
            // This solution is described here: http://serverfault.com/a/640875
            var appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", Environment.UserName, "AppData", "Roaming");
            _configPath = Path.Combine(appdata, ClientName, CleanFileName(BaseUri));
            Console.WriteLine("Config Folder: " + _configPath);
            Log.Information("Config Folder: {_configPath}", _configPath);
            Directory.CreateDirectory(_configPath);
        }

        private static void CreateSettings()
        {
            _settings = new Settings(ClientName, BaseUri);
            Log.Debug("{@_settings}", _settings);
        }

        private static int WriteBindingsFromTargetsPaged(List<Target> targets, int pageSize, int fromNumber)
        {
            do
            {
                int toNumber = fromNumber + pageSize;
                if (toNumber <= targets.Count)
                    fromNumber = WriteBindingsFomTargets(targets, toNumber, fromNumber);
                else
                    fromNumber = WriteBindingsFomTargets(targets, targets.Count + 1, fromNumber);

                if (fromNumber < targets.Count)
                {
                    WriteQuitCommandInformation();
                    string command = ReadCommandFromConsole();
                    switch (command)
                    {
                        case "q":
                            throw new Exception($"Requested to quit application");
                        default:
                            break;
                    }
                }
            } while (fromNumber < targets.Count);

            return fromNumber;
        }

        private static string ReadCommandFromConsole()
        {
            return Console.ReadLine().ToLowerInvariant();
        }

        private static void WriteQuitCommandInformation()
        {
            Console.WriteLine(" Q: Quit");
            Console.Write("Press enter to continue to next page ");
        }

        private static int WriteBindingsFomTargets(List<Target> targets, int toNumber, int fromNumber)
        {
            for (int i = fromNumber; i < toNumber; i++)
            {
                if (!Options.San)
                {
                    Console.WriteLine($" {i}: {targets[i - 1]}");
                }
                else
                {
                    Console.WriteLine($" {targets[i - 1].SiteId}: SAN - {targets[i - 1]}");
                }
                fromNumber++;
            }

            return fromNumber;
        }

        private static List<Target> GetTargetsSorted()
        {
            var targets = new List<Target>();
            if (!string.IsNullOrEmpty(Options.ManualHost))
                return targets;

            foreach (var plugin in Target.Plugins.Values)
            {
                targets.AddRange(!Options.San ? plugin.GetTargets() : plugin.GetSites());
            }

            return targets.OrderBy(p => p.ToString()).ToList();
        }

        private static void ParseCentralSslStore()
        {
            if (!string.IsNullOrWhiteSpace(Options.CentralSslStore))
            {
                Console.WriteLine("Using Centralized SSL Path: " + Options.CentralSslStore);
                Log.Information("Using Centralized SSL Path: {CentralSslStore}", Options.CentralSslStore);
                CentralSsl = true;
            }
        }

        private static void LogParsingErrorAndWaitForEnter()
        {
#if DEBUG
            Log.Debug("Program Debug Enabled");
            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
#endif
        }

        private static void ParseCertificateStore()
        {
            try
            {
                _certificateStore = Properties.Settings.Default.CertificateStore;
                Console.WriteLine("Certificate Store: " + _certificateStore);
                Log.Information("Certificate Store: {_certificateStore}", _certificateStore);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "Error reading CertificateStore from app config, defaulting to {_certificateStore} Error: {@ex}",
                    _certificateStore, ex);
            }
        }

        private static void ParseRenewalPeriod()
        {
            try
            {
                RenewalPeriod = Properties.Settings.Default.RenewalDays;
                Console.WriteLine("Renewal Period: " + RenewalPeriod);
                Log.Information("Renewal Period: {RenewalPeriod}", RenewalPeriod);
            }
            catch (Exception ex)
            {
                Log.Warning("Error reading RenewalDays from app config, defaulting to {RenewalPeriod} Error: {@ex}",
                    RenewalPeriod.ToString(), ex);
            }
        }

        private static void CreateLogger()
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.AppSettings()
                    .CreateLogger();
                Log.Information("The global logger has been configured");
            }
            catch
            {
                Console.WriteLine("Error while creating logger.");
                throw;
            }
        }

        private static string CleanFileName(string fileName)
            =>
                Path.GetInvalidFileNameChars()
                    .Aggregate(fileName, (current, c) => current.Replace(c.ToString(), string.Empty));

        public static bool PromptYesNo()
        {
            while (true)
            {
                var response = Console.ReadKey(true);
                if (response.Key == ConsoleKey.Y)
                    return true;
                if (response.Key == ConsoleKey.N)
                    return false;
                Console.WriteLine("Please press Y or N.");
            }
        }

        public static void Auto(Target binding)
        {
            var auth = Authorize(binding);
            if (auth.Status == "valid")
            {
                var pfxFilename = GetCertificate(binding);

                if (Options.Test && !Options.Renew)
                {
                    Console.WriteLine(
                        $"\nDo you want to install the .pfx into the Certificate Store/ Central SSL Store? (Y/N) ");
                    if (!PromptYesNo())
                        return;
                }

                if (!CentralSsl)
                {
                    X509Store store;
                    X509Certificate2 certificate;
                    Log.Information("Installing Non-Central SSL Certificate in the certificate store");
                    InstallCertificate(binding, pfxFilename, out store, out certificate);
                    if (Options.Test && !Options.Renew)
                    {
                        Console.WriteLine($"\nDo you want to add/update the certificate to your server software? (Y/N) ");
                        if (!PromptYesNo())
                            return;
                    }
                    Log.Information("Installing Non-Central SSL Certificate in server software");
                    binding.Plugin.Install(binding, pfxFilename, store, certificate);
                    if (!Options.KeepExisting)
                    {
                        UninstallCertificate(binding.Host, out store, certificate);
                    }
                }
                else if (!Options.Renew || !Options.KeepExisting)
                {
                    //If it is using centralized SSL, renewing, and replacing existing it needs to replace the existing binding.
                    Log.Information("Updating new Central SSL Certificate");
                    binding.Plugin.Install(binding);
                }

                if (Options.Test && !Options.Renew)
                {
                    Console.WriteLine(
                        $"\nDo you want to automatically renew this certificate in {RenewalPeriod} days? This will add a task scheduler task. (Y/N) ");
                    if (!PromptYesNo())
                        return;
                }

                if (!Options.Renew)
                {
                    Log.Information("Adding renewal for {binding}", binding);
                    ScheduleRenewal(binding);
                }
            }
        }

        public static void InstallCertificate(Target binding, string pfxFilename, out X509Store store,
            out X509Certificate2 certificate)
        {
            try
            {
                store = new X509Store(_certificateStore, StoreLocation.LocalMachine);
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (CryptographicException)
            {
                store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (Exception ex)
            {
                Log.Error("Error encountered while opening certificate store. Error: {@ex}", ex);
                throw new Exception(ex.Message);
            }

            Console.WriteLine($" Opened Certificate Store \"{store.Name}\"");
            Log.Information("Opened Certificate Store {Name}", store.Name);
            certificate = null;
            try
            {
                X509KeyStorageFlags flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;
                if (Properties.Settings.Default.PrivateKeyExportable)
                {
                    Console.WriteLine($" Set private key exportable");
                    Log.Information("Set private key exportable");
                    flags |= X509KeyStorageFlags.Exportable;
                }

                // See http://paulstovell.com/blog/x509certificate2
                certificate = new X509Certificate2(pfxFilename, Properties.Settings.Default.PFXPassword,
                    flags);

                certificate.FriendlyName =
                    $"{binding.Host} {DateTime.Now.ToString(Properties.Settings.Default.FileDateFormat)}";
                Log.Debug("{FriendlyName}", certificate.FriendlyName);

                Console.WriteLine($" Adding Certificate to Store");
                Log.Information("Adding Certificate to Store");
                store.Add(certificate);

                Console.WriteLine($" Closing Certificate Store");
                Log.Information("Closing Certificate Store");
            }
            catch (Exception ex)
            {
                Log.Error("Error saving certificate {@ex}", ex);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error saving certificate: {ex.Message.ToString()}");
                Console.ResetColor();
            }
            store.Close();
        }

        public static void UninstallCertificate(string host, out X509Store store, X509Certificate2 certificate)
        {
            try
            {
                store = new X509Store(_certificateStore, StoreLocation.LocalMachine);
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (CryptographicException)
            {
                store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (Exception ex)
            {
                Log.Error("Error encountered while opening certificate store. Error: {@ex}", ex);
                throw new Exception(ex.Message);
            }

            Console.WriteLine($" Opened Certificate Store \"{store.Name}\"");
            Log.Information("Opened Certificate Store {Name}", store.Name);
            try
            {
                X509Certificate2Collection col = store.Certificates.Find(X509FindType.FindBySubjectName, host, false);

                foreach (var cert in col)
                {
                    var subjectName = cert.Subject.Split(',');

                    if (cert.FriendlyName != certificate.FriendlyName && subjectName[0] == "CN=" + host)
                    {
                        Console.WriteLine($" Removing Certificate from Store {cert.FriendlyName}");
                        Log.Information("Removing Certificate from Store {@cert}", cert);
                        store.Remove(cert);
                    }
                }

                Console.WriteLine($" Closing Certificate Store");
                Log.Information("Closing Certificate Store");
            }
            catch (Exception ex)
            {
                Log.Error("Error removing certificate {@ex}", ex);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error removing certificate: {ex.Message.ToString()}");
                Console.ResetColor();
            }
            store.Close();
        }

        public static string GetCertificate(Target binding)
        {
            var dnsIdentifier = binding.Host;
            var sanList = binding.AlternativeNames;
            List<string> allDnsIdentifiers = new List<string>();

            if (!Options.San)
            {
                allDnsIdentifiers.Add(binding.Host);
            }
            if (binding.AlternativeNames != null)
            {
                allDnsIdentifiers.AddRange(binding.AlternativeNames);
            }

            var cp = CertificateProvider.GetProvider();
            var rsaPkp = new RsaPrivateKeyParams();
            try
            {
                if (Properties.Settings.Default.RSAKeyBits >= 1024)
                {
                    rsaPkp.NumBits = Properties.Settings.Default.RSAKeyBits;
                    Log.Debug("RSAKeyBits: {RSAKeyBits}", Properties.Settings.Default.RSAKeyBits);
                }
                else
                {
                    Log.Warning(
                        "RSA Key Bits less than 1024 is not secure. Letting ACMESharp default key bits. http://openssl.org/docs/manmaster/crypto/RSA_generate_key_ex.html");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Unable to set RSA Key Bits, Letting ACMESharp default key bits, Error: {@ex}", ex);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"Unable to set RSA Key Bits, Letting ACMESharp default key bits, Error: {ex.Message.ToString()}");
                Console.ResetColor();
            }

            var rsaKeys = cp.GeneratePrivateKey(rsaPkp);
            var csrDetails = new CsrDetails
            {
                CommonName = allDnsIdentifiers[0],
            };
            if (sanList != null)
            {
                if (sanList.Count > 0)
                {
                    csrDetails.AlternativeNames = sanList;
                }
            }
            var csrParams = new CsrParams
            {
                Details = csrDetails,
            };
            var csr = cp.GenerateCsr(csrParams, rsaKeys, Crt.MessageDigest.SHA256);

            byte[] derRaw;
            using (var bs = new MemoryStream())
            {
                cp.ExportCsr(csr, EncodingFormat.DER, bs);
                derRaw = bs.ToArray();
            }
            var derB64U = JwsHelper.Base64UrlEncode(derRaw);

            Console.WriteLine($"\nRequesting Certificate");
            Log.Information("Requesting Certificate");
            var certRequ = _client.RequestCertificate(derB64U);

            Log.Debug("certRequ {@certRequ}", certRequ);

            Console.WriteLine($" Request Status: {certRequ.StatusCode}");
            Log.Information("Request Status: {StatusCode}", certRequ.StatusCode);

            if (certRequ.StatusCode == System.Net.HttpStatusCode.Created)
            {
                var keyGenFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-gen-key.json");
                var keyPemFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-key.pem");
                var csrGenFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-gen-csr.json");
                var csrPemFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-csr.pem");
                var crtDerFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-crt.der");
                var crtPemFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-crt.pem");
                var chainPemFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-chain.pem");
                string crtPfxFile = null;
                if (!CentralSsl)
                {
                    crtPfxFile = Path.Combine(_certificatePath, $"{dnsIdentifier}-all.pfx");
                }
                else
                {
                    crtPfxFile = Path.Combine(Options.CentralSslStore, $"{dnsIdentifier}.pfx");
                }

                using (var fs = new FileStream(keyGenFile, FileMode.Create))
                    cp.SavePrivateKey(rsaKeys, fs);
                using (var fs = new FileStream(keyPemFile, FileMode.Create))
                    cp.ExportPrivateKey(rsaKeys, EncodingFormat.PEM, fs);
                using (var fs = new FileStream(csrGenFile, FileMode.Create))
                    cp.SaveCsr(csr, fs);
                using (var fs = new FileStream(csrPemFile, FileMode.Create))
                    cp.ExportCsr(csr, EncodingFormat.PEM, fs);

                Console.WriteLine($" Saving Certificate to {crtDerFile}");
                Log.Information("Saving Certificate to {crtDerFile}", crtDerFile);
                using (var file = File.Create(crtDerFile))
                    certRequ.SaveCertificate(file);

                Crt crt;
                using (FileStream source = new FileStream(crtDerFile, FileMode.Open),
                    target = new FileStream(crtPemFile, FileMode.Create))
                {
                    crt = cp.ImportCertificate(EncodingFormat.DER, source);
                    cp.ExportCertificate(crt, EncodingFormat.PEM, target);
                }

                // To generate a PKCS#12 (.PFX) file, we need the issuer's public certificate
                var isuPemFile = GetIssuerCertificate(certRequ, cp);

                using (FileStream intermediate = new FileStream(isuPemFile, FileMode.Open),
                    certificate = new FileStream(crtPemFile, FileMode.Open),
                    chain = new FileStream(chainPemFile, FileMode.Create))
                {
                    certificate.CopyTo(chain);
                    intermediate.CopyTo(chain);
                }

                Log.Debug("CentralSsl {CentralSsl} San {San}", CentralSsl.ToString(), Options.San.ToString());

                if (CentralSsl && Options.San)
                {
                    foreach (var host in allDnsIdentifiers)
                    {
                        Console.WriteLine($"Host: {host}");
                        crtPfxFile = Path.Combine(Options.CentralSslStore, $"{host}.pfx");

                        Console.WriteLine($" Saving Certificate to {crtPfxFile}");
                        Log.Information("Saving Certificate to {crtPfxFile}", crtPfxFile);
                        using (FileStream source = new FileStream(isuPemFile, FileMode.Open),
                            target = new FileStream(crtPfxFile, FileMode.Create))
                        {
                            try
                            {
                                var isuCrt = cp.ImportCertificate(EncodingFormat.PEM, source);
                                cp.ExportArchive(rsaKeys, new[] { crt, isuCrt }, ArchiveFormat.PKCS12, target,
                                    Properties.Settings.Default.PFXPassword);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Error exporting archive {@ex}", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"Error exporting archive: {ex.Message.ToString()}");
                                Console.ResetColor();
                            }
                        }
                    }
                }
                else //Central SSL and San need to save the cert for each hostname
                {
                    Console.WriteLine($" Saving Certificate to {crtPfxFile}");
                    Log.Information("Saving Certificate to {crtPfxFile}", crtPfxFile);
                    using (FileStream source = new FileStream(isuPemFile, FileMode.Open),
                        target = new FileStream(crtPfxFile, FileMode.Create))
                    {
                        try
                        {
                            var isuCrt = cp.ImportCertificate(EncodingFormat.PEM, source);
                            cp.ExportArchive(rsaKeys, new[] { crt, isuCrt }, ArchiveFormat.PKCS12, target,
                                Properties.Settings.Default.PFXPassword);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error exporting archive {@ex}", ex);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Error exporting archive: {ex.Message.ToString()}");
                            Console.ResetColor();
                        }
                    }
                }

                cp.Dispose();

                return crtPfxFile;
            }
            Log.Error("Request status = {StatusCode}", certRequ.StatusCode);
            throw new Exception($"Request status = {certRequ.StatusCode}");
        }

        public static void EnsureTaskScheduler()
        {
            var taskName = $"{ClientName} {CleanFileName(BaseUri)}";

            using (var taskService = new TaskService())
            {
                bool addTask = true;
                if (_settings.ScheduledTaskName == taskName)
                {
                    addTask = false;
                    Console.WriteLine($"\nDo you want to replace the existing {taskName} task? (Y/N) ");
                    if (!PromptYesNo())
                        return;
                    addTask = true;
                    Console.WriteLine($" Deleting existing Task {taskName} from Windows Task Scheduler.");
                    Log.Information("Deleting existing Task {taskName} from Windows Task Scheduler.", taskName);
                    taskService.RootFolder.DeleteTask(taskName, false);
                }

                if (addTask == true)
                {
                    Console.WriteLine($" Creating Task {taskName} with Windows Task Scheduler at 6am every day.");
                    Log.Information("Creating Task {taskName} with Windows Task scheduler at 6am every day.", taskName);

                    // Create a new task definition and assign properties
                    var task = taskService.NewTask();
                    task.RegistrationInfo.Description = "Check for renewal of ACME certificates.";

                    var now = DateTime.Now;
                    var runtime = new DateTime(now.Year, now.Month, now.Day, 6, 0, 0);
                    task.Triggers.Add(new DailyTrigger { DaysInterval = 1, StartBoundary = runtime });

                    var currentExec = Assembly.GetExecutingAssembly().Location;

                    // Create an action that will launch the app with the renew parameters whenever the trigger fires
                    task.Actions.Add(new ExecAction(currentExec, $"--renew --baseuri \"{BaseUri}\"",
                        Path.GetDirectoryName(currentExec)));

                    task.Principal.RunLevel = TaskRunLevel.Highest; // need admin
                    Log.Debug("{@task}", task);

                    Console.WriteLine($"\nDo you want to specify the user the task will run as? (Y/N) ");
                    if (PromptYesNo())
                    {
                        // Ask for the login and password to allow the task to run 
                        Console.Write("Enter the username (Domain\\username): ");
                        var username = Console.ReadLine();
                        Console.Write("Enter the user's password: ");
                        var password = ReadPassword();
                        Log.Debug("Creating task to run as {username}", username);
                        taskService.RootFolder.RegisterTaskDefinition(taskName, task, TaskCreation.Create, username,
                            password, TaskLogonType.Password);
                    }
                    else
                    {
                        Log.Debug("Creating task to run as current user only when the user is logged on");
                        taskService.RootFolder.RegisterTaskDefinition(taskName, task);
                    }
                    _settings.ScheduledTaskName = taskName;
                }
            }
        }


        public static void ScheduleRenewal(Target target)
        {
            EnsureTaskScheduler();

            var renewals = _settings.LoadRenewals();

            foreach (var existing in from r in renewals.ToArray() where r.Binding.Host == target.Host select r)
            {
                Console.WriteLine($" Removing existing scheduled renewal {existing}");
                Log.Information("Removing existing scheduled renewal {existing}", existing);
                renewals.Remove(existing);
            }

            var result = new ScheduledRenewal()
            {
                Binding = target,
                CentralSsl = Options.CentralSslStore,
                San = Options.San.ToString(),
                Date = DateTime.UtcNow.AddDays(RenewalPeriod),
                KeepExisting = Options.KeepExisting.ToString(),
                Script = Options.Script,
                ScriptParameters = Options.ScriptParameters,
                Warmup = Options.Warmup
            };
            renewals.Add(result);
            _settings.SaveRenewals(renewals);

            Console.WriteLine($" Renewal Scheduled {result}");
            Log.Information("Renewal Scheduled {result}", result);
        }

        public static void CheckRenewals()
        {
            Console.WriteLine("Checking Renewals");
            Log.Information("Checking Renewals");

            var renewals = _settings.LoadRenewals();
            if (renewals.Count == 0)
            {
                Console.WriteLine(" No scheduled renewals found.");
                Log.Information("No scheduled renewals found.");
            }

            var now = DateTime.UtcNow;
            foreach (var renewal in renewals)
                ProcessRenewal(renewals, now, renewal);
        }

        private static void ProcessRenewal(List<ScheduledRenewal> renewals, DateTime now, ScheduledRenewal renewal)
        {
            Console.WriteLine($" Checking {renewal}");
            Log.Information("Checking {renewal}", renewal);
            if (renewal.Date >= now) return;

            Console.WriteLine($" Renewing certificate for {renewal}");
            Log.Information("Renewing certificate for {renewal}", renewal);
            if (string.IsNullOrWhiteSpace(renewal.CentralSsl))
            {
                //Not using Central SSL
                CentralSsl = false;
                Options.CentralSslStore = null;
            }
            else
            {
                //Using Central SSL
                CentralSsl = true;
                Options.CentralSslStore = renewal.CentralSsl;
            }
            if (string.IsNullOrWhiteSpace(renewal.San))
            {
                //Not using San
                Options.San = false;
            }
            else if (renewal.San.ToLower() == "true")
            {
                //Using San
                Options.San = true;
            }
            else
            {
                //Not using San
                Options.San = false;
            }
            if (string.IsNullOrWhiteSpace(renewal.KeepExisting))
            {
                //Not using KeepExisting
                Options.KeepExisting = false;
            }
            else if (renewal.KeepExisting.ToLower() == "true")
            {
                //Using KeepExisting
                Options.KeepExisting = true;
            }
            else
            {
                //Not using KeepExisting
                Options.KeepExisting = false;
            }
            if (!string.IsNullOrWhiteSpace(renewal.Script))
            {
                Options.Script = renewal.Script;
            }
            if (!string.IsNullOrWhiteSpace(renewal.ScriptParameters))
            {
                Options.ScriptParameters = renewal.ScriptParameters;
            }
            if (renewal.Warmup) 
            {
                Options.Warmup = true;
            }
            renewal.Binding.Plugin.Renew(renewal.Binding);

            renewal.Date = DateTime.UtcNow.AddDays(RenewalPeriod);
            _settings.SaveRenewals(renewals);

            Console.WriteLine($" Renewal Scheduled {renewal}");
            Log.Information("Renewal Scheduled {renewal}", renewal);
        }

        public static string GetIssuerCertificate(CertificateRequest certificate, CertificateProvider cp)
        {
            var linksEnum = certificate.Links;
            if (linksEnum != null)
            {
                var links = new LinkCollection(linksEnum);
                var upLink = links.GetFirstOrDefault("up");
                if (upLink != null)
                {
                    var temporaryFileName = Path.GetTempFileName();
                    try
                    {
                        using (var web = new WebClient())
                        {
                            var uri = new Uri(new Uri(BaseUri), upLink.Uri);
                            web.DownloadFile(uri, temporaryFileName);
                        }

                        var cacert = new X509Certificate2(temporaryFileName);
                        var sernum = cacert.GetSerialNumberString();

                        var cacertDerFile = Path.Combine(_certificatePath, $"ca-{sernum}-crt.der");
                        var cacertPemFile = Path.Combine(_certificatePath, $"ca-{sernum}-crt.pem");

                        if (!File.Exists(cacertDerFile))
                            File.Copy(temporaryFileName, cacertDerFile, true);

                        Console.WriteLine($" Saving Issuer Certificate to {cacertPemFile}");
                        Log.Information("Saving Issuer Certificate to {cacertPemFile}", cacertPemFile);
                        if (!File.Exists(cacertPemFile))
                            using (FileStream source = new FileStream(cacertDerFile, FileMode.Open),
                                target = new FileStream(cacertPemFile, FileMode.Create))
                            {
                                var caCrt = cp.ImportCertificate(EncodingFormat.DER, source);
                                cp.ExportCertificate(caCrt, EncodingFormat.PEM, target);
                            }

                        return cacertPemFile;
                    }
                    finally
                    {
                        if (File.Exists(temporaryFileName))
                            File.Delete(temporaryFileName);
                    }
                }
            }

            return null;
        }

        public static AuthorizationState Authorize(Target target)
        {
            List<string> dnsIdentifiers = new List<string>();
            if (!Options.San)
            {
                dnsIdentifiers.Add(target.Host);
            }
            if (target.AlternativeNames != null)
            {
                dnsIdentifiers.AddRange(target.AlternativeNames);
            }
            List<AuthorizationState> authStatus = new List<AuthorizationState>();

            foreach (var dnsIdentifier in dnsIdentifiers)
            {
                var webRootPath = target.WebRootPath;

                Console.WriteLine(
                    $"\nAuthorizing Identifier {dnsIdentifier} Using Challenge Type {AcmeProtocol.CHALLENGE_TYPE_HTTP}");
                Log.Information("Authorizing Identifier {dnsIdentifier} Using Challenge Type {CHALLENGE_TYPE_HTTP}",
                    dnsIdentifier, AcmeProtocol.CHALLENGE_TYPE_HTTP);
                var authzState = _client.AuthorizeIdentifier(dnsIdentifier);
                var challenge = _client.DecodeChallenge(authzState, AcmeProtocol.CHALLENGE_TYPE_HTTP);
                var httpChallenge = challenge.Challenge as HttpChallenge;

                // We need to strip off any leading '/' in the path
                var filePath = httpChallenge.FilePath;
                if (filePath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                    filePath = filePath.Substring(1);
                var answerPath = Environment.ExpandEnvironmentVariables(Path.Combine(webRootPath, filePath));

                target.Plugin.CreateAuthorizationFile(answerPath, httpChallenge.FileContent);

                target.Plugin.BeforeAuthorize(target, answerPath, httpChallenge.Token);

                var answerUri = new Uri(httpChallenge.FileUrl);

                if (Options.Warmup) 
                {
                    Console.WriteLine($"Waiting for site to warmup...");
                    WarmupSite(answerUri);
                }

                Console.WriteLine($" Answer should now be browsable at {answerUri}");
                Log.Information("Answer should now be browsable at {answerUri}", answerUri);

                try
                {
                    Console.WriteLine(" Submitting answer");
                    Log.Information("Submitting answer");
                    authzState.Challenges = new AuthorizeChallenge[] { challenge };
                    _client.SubmitChallengeAnswer(authzState, AcmeProtocol.CHALLENGE_TYPE_HTTP, true);

                    // have to loop to wait for server to stop being pending.
                    // TODO: put timeout/retry limit in this loop
                    while (authzState.Status == "pending")
                    {
                        Console.WriteLine(" Refreshing authorization");
                        Log.Information("Refreshing authorization");
                        Thread.Sleep(4000); // this has to be here to give ACME server a chance to think
                        var newAuthzState = _client.RefreshIdentifierAuthorization(authzState);
                        if (newAuthzState.Status != "pending")
                            authzState = newAuthzState;
                    }

                    Console.WriteLine($" Authorization Result: {authzState.Status}");
                    Log.Information("Auth Result {Status}", authzState.Status);
                    if (authzState.Status == "invalid")
                    {
                        Log.Error("Authorization Failed {Status}", authzState.Status);
                        Log.Debug("Full Error Details {@authzState}", authzState);
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(
                            "\n******************************************************************************");
                        Console.WriteLine($"The ACME server was probably unable to reach {answerUri}");
                        Log.Error("Unable to reach {answerUri}", answerUri);

                        Console.WriteLine("\nCheck in a browser to see if the answer file is being served correctly.");

                        target.Plugin.OnAuthorizeFail(target);

                        Console.WriteLine(
                            "\n******************************************************************************");
                        Console.ResetColor();
                    }
                    authStatus.Add(authzState);
                }
                finally
                {
                    if (authzState.Status == "valid")
                    {
                        target.Plugin.DeleteAuthorization(answerPath, httpChallenge.Token, webRootPath, filePath);
                    }
                }
            }
            foreach (var authState in authStatus)
            {
                if (authState.Status != "valid")
                {
                    return authState;
                }
            }
            return new AuthorizationState { Status = "valid" };
        }

        // Replaces the characters of the typed in password with asterisks
        // More info: http://rajeshbailwal.blogspot.com/2012/03/password-in-c-console-application.html
        private static String ReadPassword()
        {
            var password = new StringBuilder();
            try
            {
                ConsoleKeyInfo info = Console.ReadKey(true);
                while (info.Key != ConsoleKey.Enter)
                {
                    if (info.Key != ConsoleKey.Backspace)
                    {
                        Console.Write("*");
                        password.Append(info.KeyChar);
                    }
                    else if (info.Key == ConsoleKey.Backspace)
                    {
                        if (password != null)
                        {
                            // remove one character from the list of password characters
                            password.Remove(password.Length - 1, 1);
                            // get the location of the cursor
                            int pos = Console.CursorLeft;
                            // move the cursor to the left by one character
                            Console.SetCursorPosition(pos - 1, Console.CursorTop);
                            // replace it with space
                            Console.Write(" ");
                            // move the cursor to the left by one character again
                            Console.SetCursorPosition(pos - 1, Console.CursorTop);
                        }
                    }
                    info = Console.ReadKey(true);
                }
                // add a new line because user pressed enter at the end of their password
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Reading Password {ex.Message}");
                Log.Error("Error Reading Password: {@ex}", ex);
            }

            return password.ToString();
        }

        private static void WarmupSite(Uri uri) 
        {
            var request = WebRequest.Create(uri);

            try
            {
                using (var response = request.GetResponse()) { }
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"Error warming up site: {ex.Message}");
                Log.Error("Error warming up site: {@ex}", ex);
            }
        }
    }
}
