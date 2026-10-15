using UserManagementAPI.Models;

namespace UserManagementAPI.Services;

public enum StoreStatus { Ok, NotFound, DuplicateEmail }

public readonly record struct StoreResult(StoreStatus Status, User? User = null);

public class UserStore
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int, User> _users = new();
    private readonly Dictionary<string, int> _idByEmail = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public UserStore()
    {
        Add(new UserRequest { FirstName = "Ada", LastName = "Lovelace", Email = "ada@techhive.example", Department = "IT" });
        Add(new UserRequest { FirstName = "Grace", LastName = "Hopper", Email = "grace@techhive.example", Department = "HR" });
    }

    private static void RequireNormalized(UserRequest r)
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(r.FirstName), r.FirstName), (nameof(r.LastName), r.LastName),
                     (nameof(r.Email), r.Email), (nameof(r.Department), r.Department)
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
                throw new ArgumentException($"{name} must be non-empty and trimmed; validate and Normalize() the request first.", nameof(r));
        }
    }

    public (IReadOnlyList<User> Items, int Total) GetPage(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        lock (_gate)
        {
            var offset = (long)(page - 1) * pageSize;
            var items = offset >= _users.Count
                ? []
                : _users.Values.Skip((int)offset).Take(pageSize).ToList();
            return (items, _users.Count);
        }
    }

    public virtual User? Get(int id)
    {
        lock (_gate) return _users.GetValueOrDefault(id);
    }

    public StoreResult Add(UserRequest r)
    {
        RequireNormalized(r);
        lock (_gate)
        {
            if (_idByEmail.ContainsKey(r.Email!)) return new(StoreStatus.DuplicateEmail);

            var user = new User(++_nextId, r.FirstName!, r.LastName!, r.Email!, r.Department!, DateTime.UtcNow);
            _users[user.Id] = user;
            _idByEmail[user.Email] = user.Id;
            return new(StoreStatus.Ok, user);
        }
    }

    public StoreResult Update(int id, UserRequest r)
    {
        RequireNormalized(r);
        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var existing)) return new(StoreStatus.NotFound);
            if (_idByEmail.TryGetValue(r.Email!, out var ownerId) && ownerId != id)
                return new(StoreStatus.DuplicateEmail);

            var updated = existing with
            {
                FirstName = r.FirstName!,
                LastName = r.LastName!,
                Email = r.Email!,
                Department = r.Department!
            };
            _idByEmail.Remove(existing.Email);
            _idByEmail[updated.Email] = id;
            _users[id] = updated;
            return new(StoreStatus.Ok, updated);
        }
    }

    public bool Delete(int id)
    {
        lock (_gate)
        {
            if (!_users.Remove(id, out var removed)) return false;
            _idByEmail.Remove(removed.Email);
            return true;
        }
    }
}
