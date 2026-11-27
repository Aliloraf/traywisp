using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

// Approval generations whose invalidation one PluginTrustService instance observed (a confirmed content
// change) but could not save to the trust store. See docs/trust-model.md ("Unsaved invalidations").
//
// The set can only deny: a generation in it is never authorized by the owning service, whatever the store
// and the file hold. It never grants anything and never replaces reading the store. It lives in memory only,
// so other service instances, other processes and later sessions do not know about it.
//
// It is bounded. Generations the store no longer holds are forgotten whenever the store is read (their
// approval has been removed or replaced, which is what the invalidation needed), so after such a read the
// set holds no more entries than the store holds approvals. If an entry still cannot be recorded, the set
// becomes saturated for the rest of the instance's lifetime and the service denies every plugin, rather
// than forgetting an invalidation.
internal sealed class PendingInvalidations(int capacity)
{
    private readonly Lock _gate = new();
    private readonly HashSet<ApprovalGeneration> _generations = [];
    private bool _saturated;

    public bool IsSaturated
    {
        get
        {
            lock (_gate)
            {
                return _saturated;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _generations.Count;
            }
        }
    }

    public bool Contains(ApprovalGeneration generation)
    {
        lock (_gate)
        {
            return _generations.Contains(generation);
        }
    }

    // Records an unsaved invalidation. Returns false, and saturates the set, when it is full.
    public bool Add(ApprovalGeneration generation)
    {
        lock (_gate)
        {
            if (_generations.Contains(generation))
            {
                return true;
            }

            if (_generations.Count >= capacity)
            {
                _saturated = true;
                return false;
            }

            _generations.Add(generation);
            return true;
        }
    }

    // The invalidation was saved: the approval was removed, or replaced by an explicitly granted new generation.
    public void Remove(ApprovalGeneration generation)
    {
        lock (_gate)
        {
            _generations.Remove(generation);
        }
    }

    // Forgets generations that the store, just read under the store lock, no longer holds.
    public void RetainOnly(ApprovalSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_generations.Count > 0)
            {
                HashSet<ApprovalGeneration> stored = [.. snapshot.Approvals.Select(approval => approval.Generation)];
                _generations.RemoveWhere(generation => !stored.Contains(generation));
            }
        }
    }
}
