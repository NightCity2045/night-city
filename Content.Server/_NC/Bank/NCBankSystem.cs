// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Shared._NC.Bank;
using Content.Shared.Database;
using Content.Shared._NC.Bank;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>A bank account loaded into the server. While loaded, its in-memory balance is authoritative.</summary>
public sealed class NCBankAccountRecord(int id, string number, int? profileId, ProtoId<NCBankDepartmentPrototype>? department, long balance)
{
    public readonly int Id = id;
    public readonly string Number = number;
    public readonly int? ProfileId = profileId;
    public readonly ProtoId<NCBankDepartmentPrototype>? Department = department;
    public long Balance = balance;
}

public enum NCBankTransferResult : byte
{
    Success,
    InvalidAmount,
    AccountNotFound,
    SameAccount,
    InsufficientFunds,
}

/// <summary>
/// Night City bank. Accounts are loaded into memory on first use and every change is written to the database
/// through a single ordered queue, so a loaded balance never waits on the database.
/// </summary>
public sealed partial class NCBankSystem : EntitySystem
{
    public const string ResidentFeeCategory = "resident";
    public const int MaximumFeeBasisPoints = 10000;
    public const int MaximumSalary = 100000;
    private const int HistoryLength = 50;

    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    private readonly Dictionary<int, NCBankAccountRecord> _accounts = new();
    private readonly Dictionary<int, NCBankAccountRecord> _byProfile = new();
    private readonly Dictionary<string, NCBankAccountRecord> _byDepartment = new();
    private readonly Dictionary<string, NCBankAccountRecord> _byNumber = new();
    private readonly Dictionary<string, int> _feeRates = new();
    private readonly Dictionary<(string Department, string Job), int> _salaries = new();
    private readonly Dictionary<string, NCBankDepartmentPrototype> _jobDepartments = new();

    private readonly List<NCBankEntry> _pending = new();
    private readonly Dictionary<int, List<NCBankHistoryLine>> _history = new();
    private Task _writeTask = Task.CompletedTask;
    private TimeSpan _sincePayday;

    public NCBankConfigurationPrototype Configuration { get; private set; } = default!;

    /// <summary>True once fee rates, salaries and department accounts have been loaded from the database.</summary>
    public bool Ready { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _sincePayday = TimeSpan.Zero;
            _history.Clear();
        });
        // The world session close waits for queued bank writes, both at round end and at server shutdown.
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(args => args.AddPendingWrite(FlushAsync()));

        RebuildPrototypes();
        LoadAsync();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        StartWrite();

        if (!Ready || _ticker.RunLevel != GameRunLevel.InRound)
            return;

        _sincePayday += TimeSpan.FromSeconds(frameTime);
        if (_sincePayday < Configuration.PaydayInterval)
            return;

        _sincePayday = TimeSpan.Zero;
        PaySalaries();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCBankConfigurationPrototype>() || args.WasModified<NCBankDepartmentPrototype>() ||
            args.WasModified<DepartmentPrototype>())
        {
            RebuildPrototypes();
        }
    }

    private void RebuildPrototypes()
    {
        Configuration = _prototypes.Index<NCBankConfigurationPrototype>(NCBankConfigurationPrototype.DefaultId);
        _jobDepartments.Clear();
        foreach (var bankDepartment in _prototypes.EnumeratePrototypes<NCBankDepartmentPrototype>())
        {
            if (bankDepartment.Department is not { } departmentId || !_prototypes.TryIndex(departmentId, out var department))
                continue;

            foreach (var job in department.Roles)
                _jobDepartments[job] = bankDepartment;
        }
    }

    private async void LoadAsync()
    {
        try
        {
            foreach (var (category, basisPoints) in await _db.GetNCBankFeeRatesAsync())
                _feeRates[category] = basisPoints;

            foreach (var salary in await _db.GetNCBankSalariesAsync())
                _salaries[(salary.DepartmentId, salary.JobId)] = salary.Amount;

            foreach (var department in _prototypes.EnumeratePrototypes<NCBankDepartmentPrototype>())
            {
                var opened = await _db.GetOrCreateNCDepartmentBankAccountAsync(department.ID, department.OpeningBalance);
                Cache(opened.Account);
            }

            Ready = true;
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to load the bank: {exception}");
        }
    }

    #region Accounts

    private NCBankAccountRecord Cache(NCBankAccountData data)
    {
        // A loaded account is authoritative: a fresh database read may predate writes still in the queue.
        if (_accounts.TryGetValue(data.Id, out var existing))
            return existing;

        ProtoId<NCBankDepartmentPrototype>? department = data.DepartmentId == null ? null : data.DepartmentId;
        var account = new NCBankAccountRecord(data.Id, data.Number, data.ProfileId, department, data.Balance);
        _accounts[account.Id] = account;
        _byNumber[account.Number] = account;
        if (account.ProfileId is { } profileId)
            _byProfile[profileId] = account;
        if (account.Department is { } departmentId)
            _byDepartment[departmentId] = account;
        return account;
    }

    public bool TryGetDepartmentAccount(ProtoId<NCBankDepartmentPrototype> department, out NCBankAccountRecord account)
    {
        return _byDepartment.TryGetValue(department, out account!);
    }

    /// <summary>The account of the character a player-controlled entity belongs to, once it has been loaded.</summary>
    public bool TryGetCharacterAccount(EntityUid user, out NCBankAccountRecord account)
    {
        account = default!;
        return _minds.TryGetMind(user, out var mindId, out _) &&
               TryComp<NCCharacterIdentityComponent>(mindId, out var identity) &&
               _byProfile.TryGetValue(identity.CharacterId.Value, out account!);
    }

    /// <summary>Finds an account by number, loading it from the database when needed.</summary>
    public async Task<NCBankAccountRecord?> GetAccountByNumberAsync(string number)
    {
        var normalized = number.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return null;

        if (_byNumber.TryGetValue(normalized, out var cached))
            return cached;

        var data = await _db.GetNCBankAccountByNumberAsync(normalized);
        return data == null ? null : Cache(data);
    }

    public string? GetDepartmentName(NCBankAccountRecord account)
    {
        return account.Department is { } department && GetAccountName(department) is { } name
            ? Loc.GetString(name)
            : null;
    }

    /// <summary>The localization id naming an organization account.</summary>
    public string? GetAccountName(ProtoId<NCBankDepartmentPrototype> id)
    {
        if (!_prototypes.TryIndex(id, out var bankDepartment))
            return null;

        if (bankDepartment.Name is { } name)
            return name.Id;

        if (bankDepartment.Department is { } department && _prototypes.TryIndex(department, out var prototype))
            return prototype.Name;

        return null;
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) || !characterId.IsValid)
            return;

        var profileId = characterId.Value;

        var mob = args.Mob;
        var session = args.Player;
        try
        {
            var opened = await _db.GetOrCreateNCCharacterBankAccountAsync(profileId, Configuration.CharacterOpeningBalance);
            if (opened == null)
                return;

            var account = Cache(opened.Account);
            if (opened.Created && !Deleted(mob))
                GiveCash(mob, Configuration.CharacterOpeningCash);

            _chat.DispatchServerMessage(session, Loc.GetString("nc-bank-spawn-account",
                ("number", account.Number),
                ("balance", account.Balance)));
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to open the bank account of profile {profileId}: {exception}");
        }
    }


    #endregion

    #region Fees and salaries

    public string GetFeeCategory(NCBankAccountRecord account)
    {
        return account.Department?.Id ?? ResidentFeeCategory;
    }

    public int GetFeeBasisPoints(string category)
    {
        if (_feeRates.TryGetValue(category, out var basisPoints))
            return basisPoints;

        return _prototypes.TryIndex<NCBankDepartmentPrototype>(category, out var department)
            ? department.FeeBasisPoints
            : Configuration.ResidentFeeBasisPoints;
    }

    public int GetFeeBasisPoints(NCBankAccountRecord account)
    {
        return GetFeeBasisPoints(GetFeeCategory(account));
    }

    public bool TrySetFeeBasisPoints(string category, int basisPoints)
    {
        if (basisPoints is < 0 or > MaximumFeeBasisPoints ||
            category != ResidentFeeCategory && !_prototypes.HasIndex<NCBankDepartmentPrototype>(category))
        {
            return false;
        }

        _feeRates[category] = basisPoints;
        Persist(_db.SetNCBankFeeRateAsync(category, basisPoints), "fee rate");
        return true;
    }

    public int GetSalary(ProtoId<NCBankDepartmentPrototype> department, ProtoId<JobPrototype> job)
    {
        if (_salaries.TryGetValue((department, job), out var amount))
            return amount;

        return _prototypes.TryIndex(department, out var prototype) && prototype.Salaries.TryGetValue(job, out var fallback)
            ? fallback
            : 0;
    }

    /// <summary>The department jobs whose salary the department pays, in department order.</summary>
    public IEnumerable<ProtoId<JobPrototype>> GetDepartmentJobs(ProtoId<NCBankDepartmentPrototype> department)
    {
        if (!_prototypes.TryIndex(department, out var bankDepartment) ||
            bankDepartment.Department is not { } departmentId ||
            !_prototypes.TryIndex(departmentId, out var prototype))
        {
            return [];
        }

        return prototype.Roles;
    }

    public bool TrySetSalary(ProtoId<NCBankDepartmentPrototype> department, ProtoId<JobPrototype> job, int amount)
    {
        if (amount is < 0 or > MaximumSalary || !GetDepartmentJobs(department).Contains(job))
            return false;

        _salaries[(department, job)] = amount;
        Persist(_db.SetNCBankSalaryAsync(department, job, amount), "salary");
        return true;
    }

    public bool IsManager(EntityUid user, ProtoId<NCBankDepartmentPrototype> department)
    {
        return _prototypes.TryIndex(department, out var prototype) &&
               _minds.TryGetMind(user, out var mindId, out _) &&
               _jobs.MindTryGetJobId(mindId, out var job) &&
               job is { } jobId &&
               prototype.Managers.Contains(jobId);
    }

    #endregion

    #region Operations

    /// <summary>Deposits cash. The payer's fee is taken from the cash; the rest is credited.</summary>
    public bool TryDeposit(NCBankAccountRecord account, long cash, EntityUid? actor, out long credited)
    {
        credited = 0;
        if (cash <= 0 || cash > Configuration.MaximumOperation)
            return false;

        var fee = NCBankFees.Calculate(cash, GetFeeBasisPoints(account));
        credited = cash - fee;
        Apply(account, NCBankTransactionKind.CashDeposit, credited, fee, null, string.Empty);
        _adminLog.Add(LogType.NCBank, LogImpact.Low,
            $"{ToPrettyString(actor):actor} deposited {cash} cash into {account.Number} ({credited} credited, {fee} fee)");
        return true;
    }

    public bool TryWithdraw(NCBankAccountRecord account, long amount, EntityUid? actor)
    {
        if (amount <= 0 || amount > Configuration.MaximumOperation || account.Balance < amount)
            return false;

        Apply(account, NCBankTransactionKind.CashWithdrawal, -amount, 0, null, string.Empty);
        _adminLog.Add(LogType.NCBank, LogImpact.Low,
            $"{ToPrettyString(actor):actor} withdrew {amount} cash from {account.Number}");
        return true;
    }

    /// <summary>Spawns cash and puts it in the user's hands, or at their feet.</summary>
    public void GiveCashTo(EntityUid user, int amount)
    {
        GiveCash(user, amount);
    }

    /// <summary>Spawns a cash stack at the given place.</summary>
    public EntityUid SpawnCash(EntityCoordinates coordinates, int amount)
    {
        return _stacks.SpawnAtPosition(amount, Configuration.CurrencyStack, coordinates);
    }

    /// <summary>Withdraws cash and puts it in the user's hands.</summary>
    public bool TryWithdrawCash(NCBankAccountRecord account, long amount, EntityUid user)
    {
        if (!TryWithdraw(account, amount, user))
            return false;

        GiveCash(user, (int) amount);
        return true;
    }

    /// <summary>
    /// Transfers money to another account. The payer's fee is charged on top of the amount and leaves the economy.
    /// </summary>
    public async Task<NCBankTransferResult> TryTransferAsync(NCBankAccountRecord payer, string targetNumber, long amount, EntityUid? actor)
    {
        if (amount <= 0 || amount > Configuration.MaximumOperation)
            return NCBankTransferResult.InvalidAmount;

        var target = await GetAccountByNumberAsync(targetNumber);
        if (target == null)
            return NCBankTransferResult.AccountNotFound;
        if (target == payer)
            return NCBankTransferResult.SameAccount;

        var feeFree = target.Department is { } department &&
                      _prototypes.TryIndex(department, out var targetPrototype) && targetPrototype.FeeFree;
        var fee = feeFree ? 0 : NCBankFees.Calculate(amount, GetFeeBasisPoints(payer));
        if (payer.Balance < amount + fee)
            return NCBankTransferResult.InsufficientFunds;

        Apply(payer, NCBankTransactionKind.TransferOut, -(amount + fee), fee, target.Number, string.Empty);
        Apply(target, NCBankTransactionKind.TransferIn, amount, 0, payer.Number, string.Empty);
        _adminLog.Add(LogType.NCBank, LogImpact.Medium,
            $"{ToPrettyString(actor):actor} transferred {amount} from {payer.Number} to {target.Number} ({fee} fee)");
        var transferred = new NCBankTransferredEvent(payer, target, amount);
        RaiseLocalEvent(ref transferred);
        return NCBankTransferResult.Success;
    }

    /// <summary>Admin grant (positive) or withdrawal (negative). A withdrawal cannot take the balance below zero.</summary>
    public async Task<NCBankAccountRecord?> TryAdminAdjustAsync(string number, long amount, string reason, string admin)
    {
        if (amount == 0 || Math.Abs(amount) > Configuration.MaximumOperation * 1000)
            return null;

        var account = await GetAccountByNumberAsync(number);
        if (account == null || account.Balance + amount < 0)
            return null;

        var kind = amount > 0 ? NCBankTransactionKind.AdminGrant : NCBankTransactionKind.AdminWithdrawal;
        Apply(account, kind, amount, 0, null, reason);
        _adminLog.Add(LogType.NCBank, LogImpact.High,
            $"{admin} adjusted {account.Number} by {amount}: {reason}");
        return account;
    }

    /// <summary>
    /// Pays a fulfilled corporate contract: the field department receives its share and the corporation
    /// account the rest. No bank fee applies.
    /// </summary>
    public bool TryPayContract(
        ProtoId<NCBankDepartmentPrototype> department,
        ProtoId<NCBankDepartmentPrototype> corporation,
        long reward,
        float departmentShare,
        string contract,
        EntityUid? actor)
    {
        if (reward <= 0 ||
            !_byDepartment.TryGetValue(department, out var departmentAccount) ||
            !_byDepartment.TryGetValue(corporation, out var corporationAccount))
        {
            return false;
        }

        var departmentAmount = (long) Math.Round(reward * Math.Clamp(departmentShare, 0f, 1f));
        Apply(departmentAccount, NCBankTransactionKind.ContractReward, departmentAmount, 0, null, contract);
        Apply(corporationAccount, NCBankTransactionKind.ContractReward, reward - departmentAmount, 0, null, contract);
        _adminLog.Add(LogType.NCBank, LogImpact.Medium,
            $"{ToPrettyString(actor):actor} fulfilled contract {contract}: {departmentAmount} to {departmentAccount.Number}, {reward - departmentAmount} to {corporationAccount.Number}");
        return true;
    }

    /// <summary>The loaded account of a character, by profile.</summary>
    public bool TryGetProfileAccount(int profileId, out NCBankAccountRecord account)
    {
        return _byProfile.TryGetValue(profileId, out account!);
    }

    /// <summary>Money from an unnamed sender lands on a character account (a fixer's dirty money).</summary>
    public bool TryCreditAnonymous(NCBankAccountRecord account, long amount, string note)
    {
        if (amount <= 0)
            return false;

        Apply(account, NCBankTransactionKind.TransferIn, amount, 0, null, note);
        _adminLog.Add(LogType.NCBank, LogImpact.Medium, $"{amount} anonymous money credited to {account.Number}: {note}");
        return true;
    }

    /// <summary>Takes up to the given amount back from an account, never below zero. Returns what was taken.</summary>
    public long TakeBack(NCBankAccountRecord account, long amount, string note)
    {
        var taken = Math.Min(Math.Max(0, amount), account.Balance);
        if (taken <= 0)
            return 0;

        Apply(account, NCBankTransactionKind.TransferOut, -taken, 0, null, note);
        _adminLog.Add(LogType.NCBank, LogImpact.Medium, $"{taken} taken back from {account.Number}: {note}");
        return taken;
    }

    /// <summary>Pays a fixer's reward to a loaded character account. No bank fee applies.</summary>
    public bool TryPayFixer(int profileId, long amount, string mission)
    {
        if (amount <= 0 || !_byProfile.TryGetValue(profileId, out var account))
            return false;

        Apply(account, NCBankTransactionKind.FixerPayment, amount, 0, null, mission);
        _adminLog.Add(LogType.NCBank, LogImpact.Low, $"Fixer mission {mission} paid {amount} to {account.Number}");
        return true;
    }

    /// <summary>Pays for a shop purchase from an account. No bank fee applies; the money leaves the economy.</summary>
    public bool TryPayPurchase(NCBankAccountRecord account, long amount, string item, EntityUid? actor)
    {
        if (amount <= 0 || account.Balance < amount)
            return false;

        Apply(account, NCBankTransactionKind.Purchase, -amount, 0, null, item);
        _adminLog.Add(LogType.NCBank, LogImpact.Low,
            $"{ToPrettyString(actor):actor} paid {amount} for {item} from {account.Number}");
        return true;
    }

    /// <summary>Charges rent to a character account. No bank fee applies; the money leaves the economy.</summary>
    public bool TryPayRent(NCBankAccountRecord account, long amount, string property, EntityUid? actor)
    {
        if (amount <= 0 || account.Balance < amount)
            return false;

        Apply(account, NCBankTransactionKind.Rent, -amount, 0, null, property);
        _adminLog.Add(LogType.NCBank, LogImpact.Low,
            $"{ToPrettyString(actor):actor} paid {amount} rent for {property} from {account.Number}");
        return true;
    }

    /// <summary>This round's operations on the account, newest first. History is not stored between rounds.</summary>
    public List<NCBankHistoryLine> GetHistory(NCBankAccountRecord account, int limit = 30)
    {
        if (!_history.TryGetValue(account.Id, out var lines))
            return [];

        return Enumerable.Reverse(lines).Take(limit).ToList();
    }

    private void Apply(NCBankAccountRecord account, NCBankTransactionKind kind, long amount, long fee, string? counterparty, string note)
    {
        if (amount != 0)
        {
            account.Balance += amount;
            _pending.Add(new NCBankEntry(account.Id, amount));
        }

        if (!_history.TryGetValue(account.Id, out var lines))
            _history[account.Id] = lines = new List<NCBankHistoryLine>();

        lines.Add(new NCBankHistoryLine(kind, amount, fee, counterparty, note, _ticker.RoundDuration()));
        if (lines.Count > HistoryLength)
            lines.RemoveAt(0);
    }

    private void GiveCash(EntityUid user, int amount)
    {
        if (amount <= 0)
            return;

        var cash = _stacks.SpawnAtPosition(amount, Configuration.CurrencyStack, Transform(user).Coordinates);
        _hands.PickupOrDrop(user, cash);
    }

    #endregion

    #region Payday

    private void PaySalaries()
    {
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } mob ||
                !_minds.TryGetMind(session, out var mindId, out var mind) ||
                mind.OwnedEntity != mob ||
                _mobState.IsDead(mob) ||
                !_jobs.MindTryGetJobId(mindId, out var job) ||
                job is not { } jobId ||
                !_jobDepartments.TryGetValue(jobId, out var department) ||
                !_byDepartment.TryGetValue(department.ID, out var departmentAccount) ||
                !TryGetCharacterAccount(mob, out var employee))
            {
                continue;
            }

            var salary = GetSalary(department.ID, jobId);
            if (salary <= 0)
                continue;

            if (departmentAccount.Balance < salary)
            {
                Apply(departmentAccount, NCBankTransactionKind.SalaryMissed, 0, 0, employee.Number, jobId);
                _chat.DispatchServerMessage(session, Loc.GetString("nc-bank-salary-missed"));
                continue;
            }

            Apply(departmentAccount, NCBankTransactionKind.Salary, -salary, 0, employee.Number, jobId);
            Apply(employee, NCBankTransactionKind.Salary, salary, 0, departmentAccount.Number, jobId);
            _chat.DispatchServerMessage(session, Loc.GetString("nc-bank-salary-paid",
                ("amount", salary),
                ("balance", employee.Balance)));
        }
    }

    #endregion

    #region Database queue

    private void StartWrite()
    {
        if (_pending.Count == 0 || !_writeTask.IsCompleted)
            return;

        var batch = _pending.ToList();
        _pending.Clear();
        _writeTask = WriteAsync(batch);
    }

    private async Task WriteAsync(List<NCBankEntry> batch)
    {
        try
        {
            await _db.ApplyNCBankEntriesAsync(batch);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to write {batch.Count} bank entries: {exception}");
        }
    }

    /// <summary>Writes everything queued so far.</summary>
    public async Task FlushAsync()
    {
        await _writeTask;
        StartWrite();
        await _writeTask;
    }

    private async void Persist(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to save bank {what}: {exception}");
        }
    }

    #endregion
}
