namespace SimPle.Application.Friends;

public enum AddFriendshipOutcome { Added, Conflict }
public enum UpdateFriendshipOutcome { Updated, ConcurrencyConflict }

// Conflict = unique violation (already blocked)
// ConcurrencyConflict = xmin mismatch on the friendship edge being cancelled atomically
public enum AddBlockOutcome { Added, Conflict, ConcurrencyConflict }
