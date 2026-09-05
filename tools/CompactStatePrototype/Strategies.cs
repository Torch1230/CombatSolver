namespace CompactStatePrototype;

internal sealed class DeepCopyState : IBranchState
{
    private long[] _words;
    private readonly long[]?[] _parents = new long[32][];

    public DeepCopyState(RootSnapshot root) : this(root, root.CreateInitialState()) { }
    private DeepCopyState(RootSnapshot root, long[] words) { Root = root; _words = words; }
    public RootSnapshot Root { get; }
    public string Name => "DeepCopy";
    public int TransactionDepth { get; private set; }
    public long Read(int word) => _words[word];
    public void Write(int word, long value) => _words[word] = value;

    public void BeginBranch()
    {
        long[] child = (long[])_words.Clone();
        _parents[TransactionDepth++] = _words;
        _words = child;
    }

    public void CommitBranch()
    {
        RequireTransaction();
        _parents[--TransactionDepth] = null;
    }

    public void RollbackBranch()
    {
        RequireTransaction();
        _words = _parents[--TransactionDepth]!;
        _parents[TransactionDepth] = null;
    }

    public IBranchState ForkRetained() => new DeepCopyState(Root, (long[])_words.Clone());
    private void RequireTransaction()
    {
        if (TransactionDepth == 0)
            throw new InvalidOperationException("No active branch transaction.");
    }
}

internal sealed class UndoJournalState : IBranchState
{
    private readonly record struct UndoEntry(int Word, long PreviousValue);
    private readonly long[] _words;
    private UndoEntry[] _journal = [];
    private int _journalCount;
    private readonly int[] _markers = new int[32];

    public UndoJournalState(RootSnapshot root) : this(root, root.CreateInitialState()) { }
    private UndoJournalState(RootSnapshot root, long[] words) { Root = root; _words = words; }
    public RootSnapshot Root { get; }
    public string Name => "UndoJournal";
    public int TransactionDepth { get; private set; }
    public long Read(int word) => _words[word];

    public void Write(int word, long value)
    {
        if (TransactionDepth > 0)
        {
            if (_journalCount == _journal.Length)
                Array.Resize(ref _journal, Math.Max(64, _journal.Length * 2));
            _journal[_journalCount++] = new UndoEntry(word, _words[word]);
        }
        _words[word] = value;
    }

    public void BeginBranch() => _markers[TransactionDepth++] = _journalCount;

    public void CommitBranch()
    {
        RequireTransaction();
        if (--TransactionDepth == 0)
            _journalCount = 0;
        // A committed inner scope remains undoable by its enclosing branch.
    }

    public void RollbackBranch()
    {
        RequireTransaction();
        int marker = _markers[--TransactionDepth];
        while (_journalCount > marker)
        {
            UndoEntry entry = _journal[--_journalCount];
            _words[entry.Word] = entry.PreviousValue;
        }
    }

    // Retaining multiple frontier states requires an independent image. Journaling alone
    // does not provide persistent snapshots or a parallel branch owner.
    public IBranchState ForkRetained() => new UndoJournalState(Root, (long[])_words.Clone());
    private void RequireTransaction()
    {
        if (TransactionDepth == 0)
            throw new InvalidOperationException("No active branch transaction.");
    }
}

internal sealed class PageCowState : IBranchState
{
    internal const int PageWords = 64;
    private sealed class Page(long[] words)
    {
        public readonly long[] Words = words;
        public bool Shared;
    }

    private Page[] _pages;
    private readonly Page[]?[] _parents = new Page[32][];

    public PageCowState(RootSnapshot root)
    {
        Root = root;
        long[] initial = root.CreateInitialState();
        _pages = new Page[(initial.Length + PageWords - 1) / PageWords];
        for (int page = 0; page < _pages.Length; page++)
        {
            int length = Math.Min(PageWords, initial.Length - page * PageWords);
            long[] words = new long[length];
            Array.Copy(initial, page * PageWords, words, 0, length);
            _pages[page] = new Page(words);
        }
    }

    private PageCowState(RootSnapshot root, Page[] pages) { Root = root; _pages = pages; }
    public RootSnapshot Root { get; }
    public string Name => "PageCOW";
    public int TransactionDepth { get; private set; }
    public long Read(int word) => _pages[word / PageWords].Words[word % PageWords];

    public void Write(int word, long value)
    {
        int index = word / PageWords;
        Page page = _pages[index];
        if (page.Shared)
            _pages[index] = page = new Page((long[])page.Words.Clone());
        page.Words[word % PageWords] = value;
    }

    private Page[] ForkPages()
    {
        foreach (Page page in _pages)
            page.Shared = true;
        return (Page[])_pages.Clone();
    }

    public void BeginBranch()
    {
        Page[] child = ForkPages();
        _parents[TransactionDepth++] = _pages;
        _pages = child;
    }

    public void CommitBranch()
    {
        RequireTransaction();
        _parents[--TransactionDepth] = null;
    }

    public void RollbackBranch()
    {
        RequireTransaction();
        _pages = _parents[--TransactionDepth]!;
        _parents[TransactionDepth] = null;
    }

    public IBranchState ForkRetained() => new PageCowState(Root, ForkPages());
    private void RequireTransaction()
    {
        if (TransactionDepth == 0)
            throw new InvalidOperationException("No active branch transaction.");
    }
}
