namespace IOpenPod.DeviceRegistry;

/// <summary>
/// 设备注册表：把归一化后的证据与不可变的 iPod 目录进行比对，得出识别结果。
/// 本类型不进行任何硬件探测或文件系统访问。
/// </summary>
public sealed class DeviceRegistry
{
    private sealed record ExactClaim(DeviceProfile Profile, IdentifierSummary Summary);

    private sealed record UsbObservation(UsbProductDefinition Definition, IdentifierSummary Summary);

    private readonly IReadOnlyList<DeviceProfile> _profiles;
    private readonly Dictionary<string, DeviceProfile> _profilesByModel;
    private readonly Dictionary<ModelIdentity, List<DeviceProfile>> _profilesByIdentity;
    private readonly Dictionary<string, DeviceProfile> _serialSuffixes;
    private readonly int[] _serialSuffixLengths;
    private readonly Dictionary<UsbIdentifier, UsbProductDefinition> _usbProducts;
    private readonly Dictionary<UsbIdentifier, DeviceProfile[]> _usbCandidates;

    public DeviceRegistry(RegistryCatalog catalog)
    {
        var profilesByModel = new Dictionary<string, DeviceProfile>(StringComparer.Ordinal);
        var profilesByIdentity = new Dictionary<ModelIdentity, List<DeviceProfile>>();

        foreach (var profile in catalog.Profiles)
        {
            var modelNumber = Identifiers.NormalizeModelNumber(profile.ModelNumber);
            if (modelNumber != profile.ModelNumber)
            {
                throw new InvalidOperationException($"目录中的型号编号不是规范写法：{profile.ModelNumber}");
            }

            if (!profilesByModel.TryAdd(modelNumber, profile))
            {
                throw new InvalidOperationException($"目录中存在重复型号编号：{modelNumber}");
            }

            var identity = new ModelIdentity(profile.Family, profile.Generation);
            if (!profilesByIdentity.TryGetValue(identity, out var list))
            {
                list = [];
                profilesByIdentity[identity] = list;
            }

            list.Add(profile);
        }

        var suffixes = new Dictionary<string, DeviceProfile>(StringComparer.Ordinal);
        foreach (var definition in catalog.SerialSuffixes)
        {
            var suffix = Identifiers.NormalizeSerial(definition.Suffix);
            if (suffix.Length is not (3 or 4))
            {
                throw new InvalidOperationException($"序列号后缀必须是 3 或 4 个字符：{suffix}");
            }

            if (!suffixes.TryAdd(suffix, null!))
            {
                throw new InvalidOperationException($"目录中存在重复序列号后缀：{suffix}");
            }

            if (!profilesByModel.TryGetValue(
                    Identifiers.NormalizeModelNumber(definition.ModelNumber), out var profile))
            {
                throw new InvalidOperationException(
                    $"序列号后缀 {suffix} 指向未知型号：{definition.ModelNumber}");
            }

            suffixes[suffix] = profile;
        }

        var usbProducts = new Dictionary<UsbIdentifier, UsbProductDefinition>();
        var usbCandidates = new Dictionary<UsbIdentifier, DeviceProfile[]>();
        foreach (var definition in catalog.UsbProducts)
        {
            if (!usbProducts.TryAdd(definition.Identifier, definition))
            {
                throw new InvalidOperationException(
                    $"目录中存在重复 USB 定义：{Identifiers.FormatUsbIdentifier(definition.Identifier)}");
            }

            var candidates = new List<DeviceProfile>();
            foreach (var identity in definition.CandidateIdentities)
            {
                if (profilesByIdentity.TryGetValue(identity, out var list))
                {
                    candidates.AddRange(list);
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"USB 定义没有对应的目录候选：{Identifiers.FormatUsbIdentifier(definition.Identifier)}");
            }

            usbCandidates[definition.Identifier] = candidates.ToArray();
        }

        _profiles = catalog.Profiles;
        _profilesByModel = profilesByModel;
        _profilesByIdentity = profilesByIdentity;
        _serialSuffixes = suffixes;
        _serialSuffixLengths = suffixes.Keys.Select(key => key.Length).Distinct().OrderDescending().ToArray();
        _usbProducts = usbProducts;
        _usbCandidates = usbCandidates;
    }

    /// <summary>使用内置目录构造注册表。</summary>
    public static DeviceRegistry CreateDefault() => new(DeviceCatalog.Default);

    /// <summary>按稳定显示顺序返回不可变目录。</summary>
    public IReadOnlyList<DeviceProfile> Profiles => _profiles;

    /// <summary>按型号编号精确查找设备档案。</summary>
    public DeviceProfile? ProfileForModelNumber(string modelNumber) =>
        _profilesByModel.GetValueOrDefault(Identifiers.NormalizeModelNumber(modelNumber));

    /// <summary>按家族与世代列出全部候选档案。</summary>
    public IReadOnlyList<DeviceProfile> ProfilesForIdentity(ModelIdentity identity) =>
        _profilesByIdentity.TryGetValue(identity, out var list) ? list : [];

    /// <summary>解析全部证据并给出识别结果。</summary>
    public IdentificationResult Identify(DeviceEvidence evidence)
    {
        var exactClaims = CollectExactClaims(evidence);
        var usbObservations = CollectUsbObservations(evidence);
        var hasCurrentUsb = evidence.UsbIdentifiers.Any(
            identifier => identifier.Authority == EvidenceAuthority.CurrentHardware);
        var currentUsb = usbObservations
            .Where(observation => observation.Summary.Authority == EvidenceAuthority.CurrentHardware)
            .ToArray();

        var currentModes = currentUsb.Select(item => item.Definition.Mode).Distinct().ToArray();
        if (currentModes.Length > 1)
        {
            return new IdentificationResult(
                IdentificationStatus.Conflicting,
                ConnectionMode.Unknown,
                Issues:
                [
                    new IdentificationIssue(
                        IdentificationIssueCode.UsbIdentifiersDisagree,
                        currentUsb.Select(item => item.Summary).ToArray()),
                ]);
        }

        var connectionMode = currentModes.Length == 1 ? currentModes[0] : ConnectionMode.Unknown;
        var (exactProfile, exactIssues, exactConflict) = ResolveExactClaims(exactClaims);
        if (exactConflict)
        {
            return new IdentificationResult(
                IdentificationStatus.Conflicting, connectionMode, Issues: exactIssues);
        }

        var candidateObservations = hasCurrentUsb
            ? currentUsb
            : HighestAuthorityUsb(usbObservations);

        var (usbCandidates, usbConflict) = IntersectUsbCandidates(candidateObservations);
        IdentificationIssue[] usbIssues = usbConflict
            ?
            [
                new IdentificationIssue(
                    IdentificationIssueCode.UsbIdentifiersDisagree,
                    candidateObservations.Select(item => item.Summary).ToArray()),
            ]
            : [];

        if (connectionMode == ConnectionMode.Recovery)
        {
            var issues = exactIssues.ToList();
            issues.AddRange(usbIssues);
            if (exactProfile is not null && usbCandidates.Length > 0 &&
                !usbCandidates.Contains(exactProfile))
            {
                issues.Add(UsbProfileDisagreement(exactClaims, currentUsb));
            }

            return new IdentificationResult(
                IdentificationStatus.RecoveryMode,
                connectionMode,
                Candidates: usbCandidates,
                Issues: issues);
        }

        if (usbConflict)
        {
            return new IdentificationResult(
                IdentificationStatus.Conflicting,
                connectionMode,
                Issues: [.. exactIssues, .. usbIssues]);
        }

        if (exactProfile is not null)
        {
            if (currentUsb.Length > 0 && !usbCandidates.Contains(exactProfile))
            {
                return new IdentificationResult(
                    IdentificationStatus.Conflicting,
                    connectionMode,
                    Issues: [.. exactIssues, UsbProfileDisagreement(exactClaims, currentUsb)]);
            }

            return new IdentificationResult(
                IdentificationStatus.Exact,
                connectionMode,
                exactProfile,
                [exactProfile],
                exactIssues);
        }

        if (usbCandidates.Length > 0)
        {
            return new IdentificationResult(
                IdentificationStatus.Ambiguous, connectionMode, Candidates: usbCandidates, Issues: usbIssues);
        }

        return new IdentificationResult(
            IdentificationStatus.Unknown, connectionMode, Issues: usbIssues);
    }

    /// <summary>按产品序列号后缀推断设备档案。</summary>
    public DeviceProfile? ProfileForProductSerial(string serial)
    {
        var normalized = Identifiers.NormalizeSerial(serial);
        foreach (var length in _serialSuffixLengths)
        {
            if (normalized.Length < length)
            {
                continue;
            }

            if (_serialSuffixes.TryGetValue(normalized[^length..], out var profile))
            {
                return profile;
            }
        }

        return null;
    }

    private List<ExactClaim> CollectExactClaims(DeviceEvidence evidence)
    {
        var claims = new List<ExactClaim>();

        foreach (var identifier in evidence.ModelNumbers)
        {
            var profile = ProfileForModelNumber(identifier.Value);
            if (profile is not null)
            {
                claims.Add(new ExactClaim(
                    profile,
                    new IdentifierSummary(
                        IdentifierKind.ModelNumber,
                        Identifiers.NormalizeModelNumber(identifier.Value),
                        identifier.Source,
                        identifier.Authority)));
            }
        }

        foreach (var identifier in evidence.ProductSerials)
        {
            var profile = ProfileForProductSerial(identifier.Value);
            if (profile is not null)
            {
                claims.Add(new ExactClaim(
                    profile,
                    new IdentifierSummary(
                        IdentifierKind.ProductSerial,
                        Identifiers.NormalizeSerial(identifier.Value),
                        identifier.Source,
                        identifier.Authority)));
            }
        }

        return claims;
    }

    private List<UsbObservation> CollectUsbObservations(DeviceEvidence evidence)
    {
        var result = new List<UsbObservation>();
        foreach (var identifier in evidence.UsbIdentifiers)
        {
            if (!_usbProducts.TryGetValue(identifier.Value, out var definition))
            {
                continue;
            }

            result.Add(new UsbObservation(
                definition,
                new IdentifierSummary(
                    IdentifierKind.Usb,
                    Identifiers.FormatUsbIdentifier(identifier.Value),
                    identifier.Source,
                    identifier.Authority)));
        }

        return result;
    }

    private static UsbObservation[] HighestAuthorityUsb(List<UsbObservation> observations)
    {
        if (observations.Count == 0)
        {
            return [];
        }

        var authority = observations.Max(item => item.Summary.Authority);
        return observations.Where(item => item.Summary.Authority == authority).ToArray();
    }

    private (DeviceProfile[] Candidates, bool Conflict) IntersectUsbCandidates(
        UsbObservation[] observations)
    {
        if (observations.Length == 0)
        {
            return ([], false);
        }

        var sets = observations
            .Select(item => new HashSet<DeviceProfile>(_usbCandidates[item.Definition.Identifier]))
            .ToArray();

        var intersection = new HashSet<DeviceProfile>(sets[0]);
        foreach (var set in sets.Skip(1))
        {
            intersection.IntersectWith(set);
        }

        var candidates = intersection.OrderBy(profile => profile.ModelNumber, StringComparer.Ordinal).ToArray();
        return (candidates, intersection.Count == 0);
    }

    private static (DeviceProfile? Profile, IdentificationIssue[] Issues, bool Conflict) ResolveExactClaims(
        List<ExactClaim> claims)
    {
        if (claims.Count == 0)
        {
            return (null, [], false);
        }

        var highestAuthority = claims.Max(claim => claim.Summary.Authority);
        var strongest = claims.Where(claim => claim.Summary.Authority == highestAuthority).ToArray();
        var strongestModels = strongest.Select(claim => claim.Profile.ModelNumber).Distinct().ToArray();

        if (strongestModels.Length > 1)
        {
            return (null,
            [
                new IdentificationIssue(
                    IdentificationIssueCode.ExactIdentifiersDisagree,
                    strongest.Select(claim => claim.Summary).ToArray()),
            ], true);
        }

        var winner = strongest[0].Profile;
        var rejected = claims
            .Where(claim => claim.Profile.ModelNumber != winner.ModelNumber)
            .Select(claim => claim.Summary)
            .ToArray();

        if (rejected.Length == 0)
        {
            return (winner, [], false);
        }

        return (winner,
        [
            new IdentificationIssue(
                IdentificationIssueCode.LowerAuthorityIdentifierDisagrees,
                [strongest[0].Summary, .. rejected]),
        ], false);
    }

    private static IdentificationIssue UsbProfileDisagreement(
        List<ExactClaim> exactClaims,
        UsbObservation[] usbObservations)
    {
        var highestAuthority = exactClaims.Max(claim => claim.Summary.Authority);
        var strongest = exactClaims.First(claim => claim.Summary.Authority == highestAuthority);
        return new IdentificationIssue(
            IdentificationIssueCode.UsbIdentityDisagrees,
            [strongest.Summary, .. usbObservations.Select(item => item.Summary)]);
    }
}
