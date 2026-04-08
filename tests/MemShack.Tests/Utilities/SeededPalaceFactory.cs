using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;

namespace MemShack.Tests.Utilities;

internal static class SeededPalaceFactory
{
    public static async Task<ChromaCompatibilityVectorStore> CreateAsync(TemporaryDirectory temp)
    {
        var palacePath = temp.GetPath("palace");
        var store = new ChromaCompatibilityVectorStore(palacePath);

        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_auth",
                "The authentication module uses JWT tokens for session management. Tokens expire after 24 hours. Refresh tokens are stored in HttpOnly cookies.",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = temp.GetPath("src", "auth.py"),
                    ChunkIndex = 0,
                    AddedBy = "seed",
                    FiledAt = "2026-04-07T09:00:00",
                    Importance = 9,
                }));

        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_db",
                "The database migration adds indexed tables for customer events and keeps the backend queries fast.",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = temp.GetPath("src", "database.sql"),
                    ChunkIndex = 1,
                    AddedBy = "seed",
                    FiledAt = "2026-04-07T09:05:00",
                    Weight = 7,
                }));

        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_frontend_ui",
                "The frontend React components render account dashboards and route users through the onboarding flow.",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "frontend",
                    SourceFile = temp.GetPath("src", "dashboard.tsx"),
                    ChunkIndex = 0,
                    AddedBy = "seed",
                    FiledAt = "2026-04-07T09:10:00",
                    EmotionalWeight = 6,
                }));

        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_notes_planning_roadmap",
                "The planning notes describe the roadmap, priorities, and milestone sequencing for the next release.",
                new DrawerMetadata
                {
                    Wing = "notes",
                    Room = "planning",
                    SourceFile = temp.GetPath("notes", "roadmap.md"),
                    ChunkIndex = 0,
                    AddedBy = "seed",
                    FiledAt = "2026-04-07T09:15:00",
                    Importance = 4,
                }));

        return store;
    }
}
