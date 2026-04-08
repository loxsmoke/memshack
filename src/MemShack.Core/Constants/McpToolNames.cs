namespace MemShack.Core.Constants;

public static class McpToolNames
{
    public const string Status = "mempalace_status";
    public const string ListWings = "mempalace_list_wings";
    public const string ListRooms = "mempalace_list_rooms";
    public const string GetTaxonomy = "mempalace_get_taxonomy";
    public const string GetAaakSpec = "mempalace_get_aaak_spec";
    public const string KgQuery = "mempalace_kg_query";
    public const string KgAdd = "mempalace_kg_add";
    public const string KgInvalidate = "mempalace_kg_invalidate";
    public const string KgTimeline = "mempalace_kg_timeline";
    public const string KgStats = "mempalace_kg_stats";
    public const string Traverse = "mempalace_traverse";
    public const string FindTunnels = "mempalace_find_tunnels";
    public const string GraphStats = "mempalace_graph_stats";
    public const string Search = "mempalace_search";
    public const string CheckDuplicate = "mempalace_check_duplicate";
    public const string AddDrawer = "mempalace_add_drawer";
    public const string DeleteDrawer = "mempalace_delete_drawer";
    public const string DiaryWrite = "mempalace_diary_write";
    public const string DiaryRead = "mempalace_diary_read";

    public static readonly IReadOnlyList<string> All =
    [
        Status,
        ListWings,
        ListRooms,
        GetTaxonomy,
        GetAaakSpec,
        KgQuery,
        KgAdd,
        KgInvalidate,
        KgTimeline,
        KgStats,
        Traverse,
        FindTunnels,
        GraphStats,
        Search,
        CheckDuplicate,
        AddDrawer,
        DeleteDrawer,
        DiaryWrite,
        DiaryRead,
    ];
}
