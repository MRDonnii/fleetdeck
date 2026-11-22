using System.Diagnostics;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class SelfUpdateTests
{
    [Theory]
    [InlineData("3.16.0", "3.15.1", 1)]
    [InlineData("v3.16.0", "3.16.0", 0)]
    [InlineData("3.16", "3.16.0", 0)]
    [InlineData("3.16.0.0", "3.16.0", 0)]
    [InlineData("3.16.0+abc123", "3.16.0", 0)]
    [InlineData("3.10.0", "3.9.9", 1)]
    [InlineData("3.16.0", "3.16.0-beta.9", 1)]
    [InlineData("3.16.0-beta.2", "3.16.0-beta.10", -1)]
    [InlineData("3.16.0-beta.1", "3.16.0-alpha.5", 1)]
    [InlineData("3.16.0-beta", "3.16.0-beta.1", -1)]
    [InlineData("3.16.0-1", "3.16.0-beta", -1)]
    [InlineData("4.0.0-beta.1", "3.99.99", 1)]
    public void VersionsAreCompared(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Parse(a)!.Value.CompareTo(SemVer.Parse(b)!.Value)));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v3")]
    [InlineData("3.16.0-")]
    [InlineData("3.16.0;reboot")]
    [InlineData("3.16.0-beta..1")]
    [InlineData("3.16.0 ")]
    public void InvalidVersionsAreRejected(string v) => Assert.True(v.EndsWith(' ') ? SemVer.Parse(v) is { } x && x.ToString() == "3.16.0" : SemVer.Parse(v) is null);

    [Fact] public void VersionKeepsPrereleaseAndBase()
    {
        var v = SemVer.Parse("v3.16.0-beta.2+sha")!.Value;
        Assert.Equal("3.16.0-beta.2", v.ToString());
        Assert.Equal("3.16.0", v.Base);
        Assert.True(v.IsPrerelease);
    }

    private const string Feed = """
        [
          { "tag_name": "v3.17.0-beta.1", "name": "Fleetdeck 3.17.0 beta 1", "draft": false, "prerelease": true, "published_at": "2026-11-10T20:00:00Z",
            "html_url": "https://github.com/ejer/fleetdeck/releases/tag/v3.17.0-beta.1", "body": "Nyt:\r\n- Ting\r\n\r\nDocker-billede: `ghcr.io/ejer/fleetdeck:3.17.0-beta.1`", "assets": [] },
          { "tag_name": "v3.18.0", "name": "Kladde", "draft": true, "prerelease": false, "body": "", "assets": [] },
          { "tag_name": "nightly", "name": "Natlig", "draft": false, "prerelease": true, "body": "", "assets": [] },
          { "tag_name": "v3.16.0", "name": "", "draft": false, "prerelease": false, "published_at": "2026-11-01T10:00:00Z",
            "html_url": "https://github.com/ejer/fleetdeck/releases/tag/v3.16.0", "body": "Rettelser\nImage: $(reboot)",
            "assets": [ { "name": "noter.txt", "browser_download_url": "https://github.com/ejer/fleetdeck/releases/download/v3.16.0/noter.txt" },
                        { "name": "fleetdeck-image-3.16.0.tar.gz", "browser_download_url": "https://github.com/ejer/fleetdeck/releases/download/v3.16.0/fleetdeck-image-3.16.0.tar.gz" } ] },
          { "tag_name": "v3.15.1", "name": "Fleetdeck 3.15.1", "draft": false, "prerelease": false, "body": null, "assets": [
                        { "name": "fleetdeck-image-3.15.1.tar", "browser_download_url": "https://evil.example/x.tar" } ] }
        ]
        """;

    [Fact] public void GitHubFeedIsParsed()
    {
        var list = ReleaseFeed.ParseGitHub(Feed);
        Assert.Equal(["3.17.0-beta.1", "3.16.0", "3.15.1"], list.Select(r => r.Version));   // ingen kladde, intet "nightly"
        var beta = list[0];
        Assert.True(beta.Prerelease);
        Assert.Equal("ghcr.io/ejer/fleetdeck:3.17.0-beta.1", beta.Image);
        Assert.DoesNotContain("\r", beta.Notes);
        var stable = list[1];
        Assert.Equal("v3.16.0", stable.Name);   // tomt navn → tag
        Assert.Null(stable.Image);              // "$(reboot)" er ikke et gyldigt billede
        Assert.Equal("https://github.com/ejer/fleetdeck/releases/download/v3.16.0/fleetdeck-image-3.16.0.tar.gz", stable.ImageAssetUrl);
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), stable.Published);
        Assert.Null(list[2].ImageAssetUrl);     // kun filer fra github.com
        Assert.Equal("", list[2].Notes);
    }

    [Fact] public void ChannelsPickTheRightRelease()
    {
        var list = ReleaseFeed.ParseGitHub(Feed);
        Assert.Equal("3.16.0", ReleaseFeed.Latest(list, ReleaseFeed.Stable)!.Version);
        Assert.Equal("3.17.0-beta.1", ReleaseFeed.Latest(list, ReleaseFeed.Beta)!.Version);
        Assert.Null(ReleaseFeed.Latest([], ReleaseFeed.Stable));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void BrokenFeedThrowsJsonException(string json) => Assert.ThrowsAny<System.Text.Json.JsonException>(() => ReleaseFeed.ParseGitHub(json));

    [Fact] public void ImageTemplateIsExpandedWithLowercasePath()
    {
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck:3.16.0", UpdateScript.Expand(UpdateScript.DefaultImage, "3.16.0", "v3.16.0"));
        Assert.Equal("ghcr.io/mr-donnii/fleetdeck:v3.16.0-Beta", UpdateScript.Expand("ghcr.io/Mr-Donnii/Fleetdeck:{tag}", "3.16.0-Beta", "v3.16.0-Beta"));
        Assert.Equal("registry.local:5000/fleetdeck", UpdateScript.Expand("Registry.local:5000/Fleetdeck", "1", "v1"));
    }

    [Fact] public void ImageFromNotesIsOnlyUsedForTheSameRepositoryAsTheTemplate()
    {
        var digest = "@sha256:" + new string('a', 64);
        var same = new ReleaseInfo("3.16.0", "v3.16.0", "x", "", false, null, null, Image: "ghcr.io/mrdonnii/fleetdeck:3.16.0" + digest);
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck:3.16.0" + digest, UpdateScript.ImageFor(same, UpdateScript.DefaultImage));   // fx med digest
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck:3.16.0" + digest, UpdateScript.ImageFor(same, ""));                         // tom skabelon: standardbilledet
        // release-teksten kan ikke pege selvopdateringen på et andet registry eller billede
        var r = new ReleaseInfo("3.16.0", "v3.16.0", "x", "", false, null, null, Image: "docker.io/ejer/fleetdeck:3.16.0");
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck:3.16.0", UpdateScript.ImageFor(r, UpdateScript.DefaultImage));
        Assert.Null(UpdateScript.ImageFor(r, ""));
        Assert.Equal("docker.io/ejer/fleetdeck:3.16.0", UpdateScript.ImageFor(r, "docker.io/ejer/fleetdeck:{version}"));   // egen skabelon
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck", UpdateScript.Repository("GHCR.io/MRDonnii/Fleetdeck:3.16.0@sha256:" + new string('b', 64)));
        Assert.Equal("registry.local:5000/fleetdeck", UpdateScript.Repository("registry.local:5000/fleetdeck"));
        Assert.Equal("ghcr.io/mrdonnii/fleetdeck:3.16.0", UpdateScript.ImageFor(r with { Image = null }, UpdateScript.DefaultImage));
        Assert.Null(UpdateScript.ImageFor(r with { Image = null }, ""));                              // tom skabelon = billedfilen
        Assert.Null(UpdateScript.ImageFor(r with { LocalDir = "/winsetup/Fleetdeck" }, UpdateScript.DefaultImage));   // lokal build
    }

    [Fact] public void ImageAssetMustComeFromTheConfiguredRepository()
    {
        var r = new ReleaseInfo("3.16.0", "v3.16.0", "x", "", false, null, null, ImageAssetUrl: "https://github.com/ejer/fleetdeck/releases/download/v3.16.0/fleetdeck-image-3.16.0.tar.gz");
        Assert.Equal(r.ImageAssetUrl, UpdateScript.AssetFor(r, "ejer/fleetdeck"));
        Assert.Equal(r.ImageAssetUrl, UpdateScript.AssetFor(r, "Ejer/Fleetdeck"));
        Assert.Null(UpdateScript.AssetFor(r, "andenejer/fleetdeck"));
        Assert.Null(UpdateScript.AssetFor(r with { ImageAssetUrl = "https://github.com/ejer/fleetdeck-x/releases/download/v1/fleetdeck-image.tar" }, "ejer/fleetdeck"));
        Assert.Null(UpdateScript.AssetFor(r with { ImageAssetUrl = null }, "ejer/fleetdeck"));
    }

    [Theory]
    [InlineData("ghcr.io/ejer/fleetdeck:3.16.0", true)]
    [InlineData("fleetdeck:latest", true)]
    [InlineData("registry.local:5000/a/b/fleetdeck:1.0", true)]
    [InlineData("ghcr.io/ejer/fleetdeck@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("GHCR.io/ejer/fleetdeck:1", false)]
    [InlineData("fleetdeck:1;reboot", false)]
    [InlineData("fleetdeck:$(id)", false)]
    [InlineData("fleetdeck:1 --privileged", false)]
    [InlineData("fleetdeck|x", false)]
    [InlineData("-fleetdeck", false)]
    [InlineData("", false)]
    public void ImagesAreValidatedStrictly(string image, bool ok) => Assert.Equal(ok, UpdateScript.ValidateImage(image) is null);

    [Theory]
    [InlineData("Mr-Donnii/fleetdeck", true)]
    [InlineData("ejer/fleet.deck_2", true)]
    [InlineData("ejer", false)]
    [InlineData("ejer/repo/x", false)]
    [InlineData("../x/y", false)]
    [InlineData("ejer/repo?x=1", false)]
    public void RepoIsValidated(string repo, bool ok) => Assert.Equal(ok, UpdateScript.ValidateRepo(repo) is null);

    [Theory]
    [InlineData("/winsetup/Fleetdeck", true)]
    [InlineData("/mnt/user/PC Reinstall/Fleetdeck", true)]
    [InlineData("winsetup/Fleetdeck", false)]
    [InlineData("/winsetup/../etc", false)]
    [InlineData("/winsetup/$(id)", false)]
    [InlineData("/winsetup/a'b", false)]
    public void LocalDirIsValidated(string dir, bool ok) => Assert.Equal(ok, UpdateScript.ValidateDir(dir) is null);

    [Theory]
    [InlineData("https://github.com/ejer/fleetdeck/releases/download/v3.16.0/fleetdeck-image-3.16.0.tar.gz", true)]
    [InlineData("http://github.com/ejer/x.tar", false)]
    [InlineData("https://github.com.evil.example/x.tar", false)]
    [InlineData("https://github.com/ejer/../x.tar", false)]
    [InlineData("https://github.com/ejer/x.tar'; reboot '", false)]
    public void AssetUrlIsValidated(string url, bool ok) => Assert.Equal(ok, UpdateScript.ValidateAssetUrl(url) is null);

    private static readonly UpdatePlan Plan = new("3.16.0-20261117-030000", "Fleetdeck", "0123456789ab", "/data", "3.15.1", "3.16.0",
        "ghcr.io/ejer/fleetdeck:3.16.0", null, null, "fleetdeck:3.16.0", 5);

    [Fact] public void PlanNeedsExactlyOneImageSource()
    {
        Assert.Null(UpdateScript.Validate(Plan));
        Assert.NotNull(UpdateScript.Validate(Plan with { Image = null }));
        Assert.NotNull(UpdateScript.Validate(Plan with { AssetUrl = "https://github.com/a/b/c.tar" }));
        Assert.Null(UpdateScript.Validate(Plan with { Image = null, LocalDir = "/winsetup/Fleetdeck" }));
        Assert.NotNull(UpdateScript.Validate(Plan with { Container = "Fleetdeck;reboot" }));
        Assert.NotNull(UpdateScript.Validate(Plan with { ExpectHost = "$(id)" }));
        Assert.NotNull(UpdateScript.Validate(Plan with { ToVersion = "latest" }));
        Assert.NotNull(UpdateScript.Validate(Plan with { HealthMinutes = 0 }));
        Assert.Throws<ArgumentException>(() => UpdateScript.Worker(Plan with { Image = "x;y" }));
    }

    [Fact] public void ScriptContainsTheSettingsQuotedAndTheSafetySteps()
    {
        var s = UpdateScript.Build(Plan);
        Assert.Contains("NAME='Fleetdeck'", s);
        Assert.Contains("IMAGE='ghcr.io/ejer/fleetdeck:3.16.0'", s);
        Assert.Contains("TO_BASE='3.16.0'", s);
        Assert.Contains("EXPECT_HOST='0123456789ab'", s);
        Assert.Contains("rebuild_container", s);
        Assert.Contains("opdatering-backup", s);
        Assert.Contains("rollback", s);
        Assert.Contains("setsid", s);
        Assert.Equal(1, s.Split('\n').Count(l => l == UpdateScript.Delimiter));
        Assert.Contains("step 5-95 \"Henter", s);
        Assert.Contains("step 100-100 \"Klar – Fleetdeck genstarter nu\"", s);
        Assert.Equal(90, UpdateScript.Job(Plan).TimeoutMin);
    }

    [Fact] public void PrereleaseTargetWaitsForTheFullVersionOrTheBase()
    {
        var s = UpdateScript.Worker(Plan with { ToVersion = "3.17.0-beta.1" });
        Assert.Contains("TO_VER='3.17.0-beta.1'", s);
        Assert.Contains("TO_BASE='3.17.0'", s);
    }

    [Fact] public void ScriptsAreValidShell()
    {
        if (!File.Exists("/bin/bash")) return;   // Windows
        foreach (var body in new[]
                 {
                     UpdateScript.Build(Plan), UpdateScript.Worker(Plan),
                     UpdateScript.Build(Plan with { Image = null, LocalDir = "/winsetup/Fleetdeck" }),
                     UpdateScript.Build(Plan with { Image = null, AssetUrl = "https://github.com/ejer/fleetdeck/releases/download/v3.16.0/fleetdeck-image-3.16.0.tar.gz", ExpectHost = "" }),
                 })
        {
            var f = Path.GetTempFileName();
            try
            {
                File.WriteAllText(f, body);
                using var proc = Process.Start(new ProcessStartInfo("/bin/bash", ["-n", f]) { RedirectStandardError = true })!;
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 0, err);
            }
            finally { File.Delete(f); }
        }
    }

    /// <summary>host_path: sti i containeren → sti på værten via containerens mapper (docker er udskiftet med en attrap).</summary>
    [Theory]
    [InlineData("/data", "/mnt/user/appdata/fleetdeck")]
    [InlineData("/winsetup/Fleetdeck", "/mnt/user/PC Reinstall/WinSetup/Fleetdeck")]
    [InlineData("/datax", "")]
    public void HostPathFollowsTheMounts(string inside, string expected)
    {
        if (!File.Exists("/bin/bash")) return;
        var worker = UpdateScript.Worker(Plan);
        var fn = worker[worker.IndexOf("host_path() {", StringComparison.Ordinal)..];
        fn = fn[..(fn.IndexOf("\n}\n", StringComparison.Ordinal) + 3)];
        var script = "NAME=x\ndocker() { printf '%s\\n' '/data|/mnt/user/appdata/fleetdeck' '/winsetup|/mnt/user/PC Reinstall/WinSetup' '/app/x|/tmp/x'; }\n"
            + fn + $"host_path '{inside}' || echo ''\n";
        var psi = new ProcessStartInfo("/bin/bash", ["-c", script]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal(expected, output);
    }

    [Fact] public void ContainerIsFoundByNameWhenTheImageIsUnknown()
    {
        var info = new UnraidInfo { Containers = [
            new UnraidContainer { Name = "Fleetdeck", Image = "ejer/fd-server:1", Network = "br0" },
            new UnraidContainer { Name = "fd-prod", Image = "ejer/andet:2", Network = "br0" },
            new UnraidContainer { Name = "Plex", Image = "plexinc/pms-docker", Network = "host" },
        ] };
        var byName = Assert.Single(UpdateService.Detect([(4, "Server", info)], "ffffffffffff", []));
        Assert.Equal(("Fleetdeck", "navn"), (byName.Container, byName.Why));
        // værtsnavnet sat til containerens navn (fx hostname: i compose) vinder
        Assert.Equal("fd-prod", UpdateService.Detect([(4, "Server", info)], "fd-prod", []).First().Container);
    }

    [Fact] public void MissingContainerIsExplained()
    {
        // tom + fundet: intet problem og ingen note
        Assert.Equal((null, null), UpdateService.Explain("Server", false, 1, 3, "Fleetdeck", "0123456789ab"));
        // tom + intet fundet: scriptet finder den på værten, og dialogen forklarer hvorfor
        var (p1, n1) = UpdateService.Explain("Server", false, 1, null, "", "0123456789ab");
        Assert.Null(p1);
        Assert.Contains("agenten på Server melder ingen Docker-containere", n1);
        Assert.Contains("ingen af de 4 containere", UpdateService.Explain("Server", false, 1, 4, "", "0123456789ab").Note);
        // ingen maskine: forklarende fejl, der peger på feltet
        Assert.Contains("Docker-værten er ikke fundet", UpdateService.Explain(null, false, 2, null, "", "0123456789ab").Problem);
        Assert.Contains("Ingen Linux-maskine har agenten", UpdateService.Explain(null, false, 0, null, "", "0123456789ab").Problem);
        Assert.Contains("findes ikke længere", UpdateService.Explain(null, true, 2, null, "", "0123456789ab").Problem);
        // uden brugbart værtsnavn kan scriptet ikke finde den selv
        Assert.Contains("docker ps", UpdateService.Explain("Server", false, 1, null, "", "").Problem);
    }

    [Fact] public void EmptyContainerNeedsTheExpectedHost()
    {
        Assert.Null(UpdateScript.Validate(Plan with { Container = "" }));
        Assert.Equal(UpdateScript.MissingContainer, UpdateScript.Validate(Plan with { Container = "", ExpectHost = "" }));
        Assert.NotNull(UpdateScript.Validate(Plan with { Container = "ikke gyldig" }));
    }

    [Fact] public void OwnContainerIsFound()
    {
        var info = new UnraidInfo
        {
            Containers =
            [
                new UnraidContainer { Name = "Fleetdeck", Id = "0123456789abcdef", Image = "fleetdeck:latest", Network = "br0", Ip = "192.168.1.29" },
                new UnraidContainer { Name = "fleetdeck-mdm", Image = "ghcr.io/ejer/fleetdeck-mdm:1", Network = "br0", Ip = "192.168.1.30" },
                new UnraidContainer { Name = "fleetdeck-tunnel", Image = "cloudflare/cloudflared:2026.11.1", Network = "br0" },
                new UnraidContainer { Name = "Plex", Image = "plexinc/pms-docker", Network = "host", Ip = "192.168.1.29" },
            ],
        };
        var found = UpdateService.Detect([(4, "Server", info), (5, "Pc", null)], "0123456789ab", ["192.168.1.29"]);
        var best = Assert.Single(found);
        Assert.Equal(("Fleetdeck", 4, 190), (best.Container, best.MachineId, best.Score));
        // gammel agent uden container-id: billedet alene peger stadig på den rigtige
        info.Containers[0].Id = null;
        Assert.Equal("Fleetdeck", UpdateService.Detect([(4, "Server", info)], "ffffffffffff", []).Single().Container);
    }

    [Theory]
    [InlineData("fleetdeck:latest", true)]
    [InlineData("ghcr.io/ejer/fleetdeck:3.16.0", true)]
    [InlineData("Fleetdeck", true)]
    [InlineData("ghcr.io/ejer/fleetdeck-mdm:1", false)]
    [InlineData("myfleetdeck:1", false)]
    public void FleetdeckImagesAreRecognised(string image, bool ok) => Assert.Equal(ok, UpdateService.FleetdeckImage(image));

    [Fact] public void LocalBuildFolderIsRead()
    {
        var dir = Directory.CreateTempSubdirectory("fd-local-").FullName;
        try
        {
            Assert.Throws<FileNotFoundException>(() => ReleaseFeed.ReadLocal(dir));
            File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project><PropertyGroup><Version>3.16.0-beta.3</Version></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM scratch");
            File.WriteAllText(Path.Combine(dir, "RELEASE-NOTES.md"), "- Ny ting");
            var r = ReleaseFeed.ReadLocal(dir);
            Assert.Equal(("3.16.0-beta.3", "- Ny ting", dir), (r.Version, r.Notes, r.LocalDir));
        }
        finally { Directory.Delete(dir, true); }
    }
    // ------------------------------------------------------------------ hele forløbet med en attrap for docker
    private const string FakeDocker = """
        S="$FAKE"
        N=${FAKE_NAME:-Fleetdeck}
        key() { printf '%s' "$1" | tr ':/@' '___'; }
        cdir() { [ -d "$S/c/$1" ] && { echo "$S/c/$1"; return 0; }; local x; for x in "$S"/c/*/; do [ "$(cat "$x/id" 2>/dev/null)" = "$1" ] && { echo "${x%/}"; return 0; }; done; return 1; }
        docker() {
          local cmd=$1; shift
          case "$cmd" in
            info) return 0 ;;
            inspect)
              local fmt=""; if [ "$1" = -f ]; then fmt=$2; shift 2; fi
              local d; d=$(cdir "$1") || return 1
              case "$fmt" in
                "") echo "[{}]" ;;
                '{{.Name}}') echo "/${d##*/}" ;;
                '{{.Id}}') cat "$d/id" ;;
                '{{.Config.Hostname}}') cut -c1-12 "$d/id" ;;
                '{{.Config.Image}}') cat "$d/image" ;;
                '{{.Image}}') cat "$d/imageid" ;;
                *'.Mounts'*'.Destination'*) echo "/data|$S/data" ;;
                '{{.State.Status}}') if [ -f "$d/running" ]; then echo running; else echo exited; fi ;;
                '{{.State.Running}}') if [ -f "$d/running" ]; then echo true; else echo false; fi ;;
                '{{.HostConfig.NetworkMode}}') echo br0 ;;
                *'.IPAMConfig.IPv4Address'*) echo "br0|192.168.1.29" ;;
                '{{range .Config.Env}}{{println .}}{{end}}') printf '%s\n' PATH=/usr/bin PORT=8585 SERVER_URL=http://192.168.1.29:8585 ;;
                *'.HostConfig.Binds'*) printf '%s\n' "$S/data:/data:rw" ;;
                *'.HostConfig.RestartPolicy.Name'*) echo unless-stopped ;;
                *) ;;
              esac ;;
            image)
              shift; local fmt=""; if [ "$1" = -f ]; then fmt=$2; shift 2; fi
              [ -f "$S/i/$(key "$1")" ] || { [ -f "$S/i/$(key "$1" | sed 's/^sha256_//')" ] || return 1; }
              case "$fmt" in '{{.Id}}') cat "$S/i/$(key "$1")" ;; *'.Config.Env'*) echo PATH=/usr/bin ;; esac ;;
            pull) case "$1" in *missing*) echo "manifest unknown" >&2; return 1 ;; esac
                  printf '%s\n' "good: Pulling from ejer/fleetdeck" "aa11: Already exists" "bb22: Pulling fs layer" "cc33: Pulling fs layer" \
                    "bb22: Download complete" "cc33: Download complete" "bb22: Pull complete" "cc33: Pull complete" "Digest: sha256:00"
                  echo "sha256:$(key "$1")" > "$S/i/$(key "$1")" ;;
            stop) local n; for n; do :; done; rm -f "$S/c/$n/running" ;;
            rename) mv "$S/c/$1" "$S/c/$2" ;;
            create)
              local name="" img a; while [ $# -gt 1 ]; do a=$1; shift; [ "$a" = --name ] && { name=$1; shift; }; echo "$a" >> "$S/create-args"; done; img=$1
              mkdir -p "$S/c/$name"; echo "$img" > "$S/c/$name/image"; cat "$S/i/$(key "$img")" > "$S/c/$name/imageid"
              echo "fedcba9876543210$RANDOM" > "$S/c/$name/id"
              echo migreret > "$S/data/fleetdeck.db" ;;
            start) touch "$S/c/$1/running" ;;
            rm) [ "$1" = -f ] && shift; rm -rf "$S/c/$1" ;;
            exec)
              case "$(cat "$S/c/$1/image")" in
                *good*) echo '{"ok":true,"version":"3.16.0","release":"3.16.0"}' ;;
                *old*) return 0 ;;
                *) return 1 ;;
              esac ;;
            logs) echo "Unhandled exception" ;;
            ps) local x; for x in "$S"/c/*/; do cat "$x/id"; done ;;
            tag|network) return 0 ;;
            *) echo "fake docker: $cmd $*" >&2; return 1 ;;
          esac
        }
        date() { if [ "$1" = "+%s" ]; then local n; n=$(( $(cat "$S/clock") + 20 )); echo "$n" > "$S/clock"; echo "$n"; else command date "$@"; fi; }
        sleep() { :; }
        curl() { return 1; }
        export -f docker date sleep curl key cdir
        export S
        mkdir -p "$S/c/$N" "$S/i" "$S/data/keys" "$S/w"
        mkdir -p "$S/c/Plex"; echo "fedcba987654aaaa" > "$S/c/Plex/id"; echo plexinc/pms > "$S/c/Plex/image"
        echo 0 > "$S/clock"
        echo "ghcr.io/ejer/fleetdeck:old" > "$S/c/$N/image"; echo "sha256:old" > "$S/c/$N/imageid"
        echo "sha256:old" > "$S/i/$(key ghcr.io/ejer/fleetdeck:old)"; echo "sha256:old" > "$S/i/sha256_old"
        echo "0123456789abcdef0000" > "$S/c/$N/id"; touch "$S/c/$N/running"
        echo original > "$S/data/fleetdeck.db"; echo nøgle > "$S/data/keys/key-1.xml"
        bash "$S/run.sh" "$S/w"
        """;

    private static (int Code, string Output, string Dir) RunFake(string image, bool wrapper = false, string container = "Fleetdeck", string actualName = "Fleetdeck", string expectHost = "0123456789ab")
    {
        var dir = Directory.CreateTempSubdirectory("fd-update-").FullName;
        var plan = Plan with { Image = image, HealthMinutes = 1, Run = "test-" + Path.GetFileName(dir), Container = container, ExpectHost = expectHost };
        File.WriteAllText(Path.Combine(dir, "run.sh"), wrapper ? UpdateScript.Build(plan) : UpdateScript.Worker(plan));
        var psi = new ProcessStartInfo("/bin/bash", ["-c", FakeDocker]) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["FAKE"] = dir;
        psi.Environment["FAKE_NAME"] = actualName;
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd() + stderr.Result;
        p.WaitForExit();
        return (p.ExitCode, output, dir);
    }

    private static string? Image(string dir, string name) => File.Exists(Path.Combine(dir, "c", name, "image")) ? File.ReadAllText(Path.Combine(dir, "c", name, "image")).Trim() : null;
    private static string Db(string dir) => File.ReadAllText(Path.Combine(dir, "data", "fleetdeck.db")).Trim();

    [Fact] public void SuccessfulUpdateReplacesTheContainerAndKeepsABackup()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:good");
        try
        {
            Assert.True(code == 0, output);
            Assert.Contains("✓ Fleetdeck er opdateret fra 3.15.1 til 3.16.0", output);
            Assert.Contains("::ws 100-100 Klar – Fleetdeck genstarter nu", output);
            Assert.Contains("::ws 5-95 Henter", output);
            Assert.Contains("Henter lag: 50 %", output);   // et lag fandtes, og ét er hentet: 3 af 6 enheder
            Assert.DoesNotContain("Henter lag: 33 %", output);
            Assert.Contains("Henter lag: 100 %", output);
            Assert.True(output.IndexOf("Henter lag: 100 %", StringComparison.Ordinal) < output.IndexOf("::ws 100-100 Klar", StringComparison.Ordinal));
            Assert.Equal("ghcr.io/ejer/fleetdeck:good", Image(dir, "Fleetdeck"));
            Assert.True(File.Exists(Path.Combine(dir, "c", "Fleetdeck", "running")));
            Assert.Null(Image(dir, "Fleetdeck-fd-forrige"));   // den gamle container er ryddet op
            var bk = Assert.Single(Directory.GetDirectories(Path.Combine(dir, "data", "opdatering-backup")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(bk, "fleetdeck.db")).Trim());
            Assert.True(File.Exists(Path.Combine(bk, "keys", "key-1.xml")));
            // samme indstillinger: fast IP, mappe, eget miljø (ikke billedets PATH) og genstartspolitik
            var args = File.ReadAllLines(Path.Combine(dir, "create-args"));
            Assert.Contains("192.168.1.29", args);
            Assert.Contains("PORT=8585", args);
            Assert.Contains("SERVER_URL=http://192.168.1.29:8585", args);
            Assert.DoesNotContain("PATH=/usr/bin", args);
            Assert.Contains(dir + "/data:/data:rw", args);
            Assert.Contains("unless-stopped", args);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void EmptyContainerIsFoundOnTheHost()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:good", container: "", actualName: "fd-server");
        try
        {
            Assert.True(code == 0, output);
            Assert.Contains("✓ Fandt Fleetdeck-containeren: fd-server", output);
            Assert.Equal("ghcr.io/ejer/fleetdeck:good", Image(dir, "fd-server"));
            Assert.Equal("plexinc/pms", Image(dir, "Plex"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void EmptyContainerThatCannotBeFoundChangesNothing()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:good", container: "", expectHost: "aaaaaaaaaaaa");
        try
        {
            Assert.NotEqual(0, code);
            Assert.Contains("Fandt ikke Fleetdeck-containeren", output);
            Assert.Contains("docker ps", output);
            Assert.Equal("ghcr.io/ejer/fleetdeck:old", Image(dir, "Fleetdeck"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void WrapperRunsTheWorkerDetachedAndRelaysItsOutput()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:good", wrapper: true);
        try
        {
            Assert.True(code == 0, output);
            Assert.Contains("::ws 2-5 Kontrollerer", output);
            Assert.Contains("✓ Fleetdeck er opdateret", output);
            Assert.Equal("ghcr.io/ejer/fleetdeck:good", Image(dir, "Fleetdeck"));
        }
        finally
        {
            Directory.Delete(dir, true);
            try { Directory.Delete("/tmp/fleetdeck-opdatering/test-" + Path.GetFileName(dir), true); } catch (IOException) { }
        }
    }

    [Fact] public void FailingNewVersionIsRolledBackWithTheDatabase()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:bad");
        try
        {
            Assert.True(code == 1, output);
            Assert.Contains("Ruller tilbage", output);
            Assert.Contains("✓ Fleetdeck 3.15.1 kører igen", output);
            Assert.Equal("ghcr.io/ejer/fleetdeck:old", Image(dir, "Fleetdeck"));
            Assert.True(File.Exists(Path.Combine(dir, "c", "Fleetdeck", "running")));
            Assert.Null(Image(dir, "Fleetdeck-fd-forrige"));
            Assert.Equal("original", Db(dir));   // databasen fra før opdateringen
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void MissingImageChangesNothing()
    {
        if (!File.Exists("/bin/bash")) return;
        var (code, output, dir) = RunFake("ghcr.io/ejer/fleetdeck:missing");
        try
        {
            Assert.True(code == 1, output);
            Assert.Contains("findes ikke endnu", output);   // billedet bygges stadig på GitHub: sig det, og rør ikke den kørende version
            Assert.Equal("ghcr.io/ejer/fleetdeck:old", Image(dir, "Fleetdeck"));
            Assert.True(File.Exists(Path.Combine(dir, "c", "Fleetdeck", "running")));
            Assert.False(Directory.Exists(Path.Combine(dir, "data", "opdatering-backup")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
