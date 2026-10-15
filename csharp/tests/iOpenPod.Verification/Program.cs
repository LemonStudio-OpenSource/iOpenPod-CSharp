using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.IPodDb.Signing;
using IOpenPod.IPodDb.Signing.HashAb;

namespace DbCheck;

/// <summary>数据库层与设备层的离线校验工具。</summary>
internal static class Program
{
    private static string Fixtures { get; } = ResolveFixtureRoot();

    /// <summary>从当前程序集位置向上查找仓库中的测试夹具目录。</summary>
    private static string ResolveFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "fixtures");
    }

    /// <summary>查找随本校验项目一起保存的夹具目录（HASHAB 官方向量等）。</summary>
    private static string ResolveLocalFixtureRoot()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "fixtures");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "tests", "iOpenPod.Verification", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return direct;
    }

    private static readonly string LocalFixtures = ResolveLocalFixtureRoot();

    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        CheckITunesDbRoundTrip("iTunesDB/original-empty.b64");
        CheckITunesDbRoundTrip("iTunesDB/captured-album-index-36.b64");
        CheckArtworkDbRoundTrip("ArtworkDB/original-empty.b64");
        CheckArtworkDbRoundTrip("ArtworkDB/original-with-unknown-data.b64");
        CheckPhotosDbRoundTrip("PhotosDB/original-photo-library.b64");
        CheckHash58Vectors();
        CheckHashAbVectors();
        CheckCatalog();
        CheckSysInfoIdentification();
        CheckRoundTripThroughEdits();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    // ---- 校验项 ----

    private static void CheckITunesDbRoundTrip(string relative)
    {
        try
        {
            var bytes = LoadFixture(relative);
            var database = ITunesDatabase.Parse(bytes);
            var output = database.Serialize();

            Assert($"iTunesDB 往返一致：{relative}", bytes.AsSpan().SequenceEqual(output));
            Console.WriteLine(
                $"    版本 0x{database.Version:X}，曲目 {database.TrackCount} 首，播放列表 {database.UserPlaylists.Count} 个，" +
                $"数据集 {database.Datasets.Count} 个，字节 {bytes.Length}");

            foreach (var track in database.Tracks.Take(3))
            {
                Console.WriteLine(
                    $"      · [{track.TrackId}] {track.Title} — {track.Artist} / {track.Album} " +
                    $"（{track.FileExtension}，{track.LengthMs} ms，媒体类型 {track.MediaTypeName}）");
            }
        }
        catch (Exception error)
        {
            Fail($"iTunesDB 往返一致：{relative}", error.Message);
        }
    }

    private static void CheckArtworkDbRoundTrip(string relative)
    {
        try
        {
            var bytes = LoadFixture(relative);
            var database = ArtworkDatabase.Parse(bytes);
            var output = database.Serialize();

            Assert($"ArtworkDB 往返一致：{relative}", bytes.AsSpan().SequenceEqual(output));
            Console.WriteLine(
                $"    插图 {database.Items.Count} 张，格式文件 {database.FormatFiles.Count} 个，字节 {bytes.Length}");

            foreach (var item in database.Items.Take(3))
            {
                Console.WriteLine(
                    $"      · 图像 {item.ImageId} → 曲目 {item.DbTrackIdRef:X}，表示形式 {item.Locations.Count} 种");
            }
        }
        catch (Exception error)
        {
            Fail($"ArtworkDB 往返一致：{relative}", error.Message);
        }
    }

    private static void CheckPhotosDbRoundTrip(string relative)
    {
        try
        {
            var bytes = LoadFixture(relative);
            var database = ArtworkDatabase.Parse(bytes);
            var output = database.Serialize();
            Assert($"PhotosDB 结构往返一致：{relative}", bytes.AsSpan().SequenceEqual(output));
            Console.WriteLine($"    照片条目 {database.Items.Count} 张，字节 {bytes.Length}");
        }
        catch (Exception error)
        {
            Fail($"PhotosDB 结构往返一致：{relative}", error.Message);
        }
    }

    private static void CheckHash58Vectors()
    {
        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "writing", "hash58-original.json")));
            var index = 0;
            foreach (var vector in json.RootElement.GetProperty("vectors").EnumerateArray())
            {
                index++;
                var guid = Convert.FromHexString(vector.GetProperty("guid").GetString()!);
                var expectedHash = vector.GetProperty("hash58").GetString()!;
                var expectedOutput = vector.GetProperty("output_sha256").GetString()!;

                var data = new byte[1024];
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)((i * 37 + 11) % 256);
                }

                "mhbd"u8.CopyTo(data);
                BitConverter.TryWriteBytes(data.AsSpan(4), 244);
                BitConverter.TryWriteBytes(data.AsSpan(8), 1024);

                var signed = DatabaseSignature.SignHash58(data, guid);
                var actualHash = Convert.ToHexString(signed.AsSpan(0x58, 20)).ToLowerInvariant();
                var actualOutput = Convert.ToHexString(SHA256.HashData(signed)).ToLowerInvariant();

                Assert($"HASH58 向量 {index} 签名一致", actualHash == expectedHash,
                    $"期望 {expectedHash}，实际 {actualHash}");
                Assert($"HASH58 向量 {index} 输出一致", actualOutput == expectedOutput,
                    $"期望 {expectedOutput}，实际 {actualOutput}");
                Assert($"HASH58 向量 {index} 可验证", DatabaseSignature.VerifyHash58(signed, guid));
            }

            Console.WriteLine($"    HASH58 共校验 {index} 组官方向量（含签名与整份输出 SHA-256）。");
        }
        catch (Exception error)
        {
            Fail("HASH58 向量校验", error.Message);
        }
    }

    private static void CheckHashAbVectors()
    {
        try
        {
            var path = Path.Combine(LocalFixtures, "hashab-test-data.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("    （未找到 HASHAB 官方向量，跳过）");
                return;
            }

            var json = JsonDocument.Parse(File.ReadAllText(path));
            var index = 0;
            foreach (var vector in json.RootElement.EnumerateArray())
            {
                index++;
                var sha1 = Convert.FromHexString(vector.GetProperty("sha1").GetString()!);
                var uuid = Convert.FromHexString(vector.GetProperty("uuid").GetString()!);
                var expected = vector.GetProperty("target").GetString()!.ToLowerInvariant();
                var actual = Convert.ToHexString(HashAbCalculator.Compute(sha1, uuid)).ToLowerInvariant();
                if (actual != expected)
                {
                    Assert($"HASHAB 向量 {index}", false, $"期望 {expected}，实际 {actual}");
                    return;
                }
            }

            Assert($"HASHAB 官方向量全部一致（共 {index} 组）", true);
            Console.WriteLine($"    HASHAB 共校验 {index} 组官方向量。");
        }
        catch (Exception error)
        {
            Fail("HASHAB 向量校验", error.Message);
        }
    }

    private static void CheckCatalog()
    {
        try
        {
            var registry = DeviceRegistry.CreateDefault();
            Assert("设备目录可加载", registry.Profiles.Count > 100, $"实际 {registry.Profiles.Count} 个型号");

            var nano4 = registry.ProfileForModelNumber("MB598");
            Assert("按型号精确识别", nano4 is not null && nano4.Family == "iPod Nano",
                nano4 is null ? "未找到 MB598" : nano4.ChineseDescription);

            var classic = registry.ProfileForModelNumber("MB029");
            Assert("识别 iPod classic", classic is not null && classic.Family == "iPod Classic",
                classic?.ChineseDescription ?? "未找到 MB029");

            Console.WriteLine($"    目录型号 {registry.Profiles.Count} 个，序列号后缀与 USB 定义已加载。");
        }
        catch (Exception error)
        {
            Fail("设备目录校验", error.Message);
        }
    }

    private static void CheckSysInfoIdentification()
    {
        try
        {
            const string sysInfo = """
                FirewireGuid: 0x000A27001B4A8C2C
                ModelNumStr: xA477
                pszSerialNumber: 1A23456789012
                BoardHwName: D2
                visibleBuildID: 1.1.2
                USBProductID: 0x1263
                FamilyID: 12
                """;

            var evidence = DeviceMetadata.ParseSysInfo(Encoding.UTF8.GetBytes(sysInfo));
            var registry = DeviceRegistry.CreateDefault();
            var result = registry.Identify(evidence);

            Assert("SysInfo 可识别设备", result.Status == IdentificationStatus.Exact,
                result.ChineseStatus);
            Assert("SysInfo 识别出正确家族",
                result.Profile is not null && result.Profile.Family == "iPod Nano",
                result.Profile?.ChineseDescription ?? "无");

            var serial = evidence.TransportSerials.FirstOrDefault();
            Assert("FireWire GUID 解析正确",
                serial is not null && serial.Value == "000A27001B4A8C2C",
                serial?.Value ?? "无");

            Console.WriteLine($"    识别结果：{result.Profile?.ChineseDescription}（{result.ChineseStatus}）");
        }
        catch (Exception error)
        {
            Fail("SysInfo 识别校验", error.Message);
        }
    }

    private static void CheckRoundTripThroughEdits()
    {
        try
        {
            var bytes = LoadFixture("iTunesDB/captured-album-index-36.b64");
            var database = ITunesDatabase.Parse(bytes);
            if (database.TrackCount == 0)
            {
                Console.WriteLine("    （设备库中没有曲目，跳过编辑往返校验）");
                return;
            }

            var track = database.Tracks[0];
            var originalTitle = track.Title;
            var originalRating = track.Rating;

            track.Title = "中文标题测试";
            track.Rating = 80;
            var edited = database.Serialize();
            var reparsed = ITunesDatabase.Parse(edited);

            Assert("编辑后可重新解析", reparsed.TrackCount == database.TrackCount);
            var updated = reparsed.TrackById(track.TrackId) ?? reparsed.Tracks[0];
            Assert("标题写入生效", updated.Title == "中文标题测试", updated.Title);
            Assert("评分写入生效", updated.Rating == 80, updated.Rating.ToString());

            // 复原后应能逐字节回到原文件。
            updated.Title = originalTitle;
            updated.Rating = originalRating;
            var restored = reparsed.Serialize();
            var identical = bytes.AsSpan().SequenceEqual(restored);
            Assert("修改复原后字节完全一致", identical,
                identical ? null : DescribeDifference(bytes, restored));

            // 播放列表增删往返
            var playlist = database.AddPlaylist("测试播放列表");
            playlist.AddTrack(database.Tracks[0], database.NextPlaylistItemPersistentId());
            var withPlaylist = ITunesDatabase.Parse(database.Serialize());
            var found = withPlaylist.UserPlaylists.FirstOrDefault(p => p.Name == "测试播放列表");
            Assert("播放列表创建并可读回", found is not null && found.TrackCount == 1,
                found is null ? "未找到" : $"{found.TrackCount} 首");

            Console.WriteLine("    已完成标题/评分写入、逐字节复原与播放列表增删往返校验。");
        }
        catch (Exception error)
        {
            Fail("编辑往返校验", error.Message);
        }
    }

    // ---- 工具 ----

    private static string DescribeDifference(byte[] expected, byte[] actual)
    {
        var limit = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < limit; i++)
        {
            if (expected[i] != actual[i])
            {
                var context = Math.Max(0, i - 8);
                return $"长度 {expected.Length}/{actual.Length}，首个差异在 {i}（0x{i:X}）：" +
                       $"原始 {Convert.ToHexString(expected.AsSpan(context, Math.Min(24, expected.Length - context)))} " +
                       $"新值 {Convert.ToHexString(actual.AsSpan(context, Math.Min(24, actual.Length - context)))}";
            }
        }

        return $"长度差异 {expected.Length}/{actual.Length}";
    }

    private static byte[] LoadFixture(string relative)
    {
        var path = Path.Combine(Fixtures, relative.Replace('/', Path.DirectorySeparatorChar));
        return Convert.FromBase64String(File.ReadAllText(path));
    }

    private static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name}{(detail is null ? string.Empty : " → " + detail)}");
        }
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Console.WriteLine($"  [失败] {name} → {detail}");
    }
}
