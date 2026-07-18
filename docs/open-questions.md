# Offene Klärungen / TODO

## ✅ GELÖST (§15 Schritt 10, 2026-07-18): Manuelle Bewertungen vs. Vollpass

**Entscheidung:** Gesperrte Items (`BaseItem.IsLocked`) werden **immer** von der Anreicherung
übersprungen — im Realtime-Listener (`ListenerGate.SkipLocked` + Guard in `EnrichItemAsync`) **und** im
Vollpass (`BuildWorkItems` überspringt gesperrte Items, nimmt sie aber weiter in `liveIds` auf, damit
`PruneOrphans` ihr Backup nicht löscht). Kein Config-Flag — Sperren ist der Jellyfin-native, dauerhafte
Schutz eines manuell gesetzten Werts. Der **Restore ignoriert die Sperre bewusst** (expliziter
Admin-Reset). Live verifiziert (Vollpass `starting for 0 item(s)` bei gesperrtem Item), siehe
`V2-verification.md` (Step 10). Der historische Kontext bleibt unten stehen.

---

## Manuelle Bewertungen vs. Vollpass (ursprüngliche Fragestellung — jetzt gelöst, siehe oben)

**Kontext.** Der Realtime-Listener (§15 Schritt 9) reagiert bewusst **nur auf automatische** Metadaten-
Updates (`MetadataDownload` / `MetadataImport`) und auf neu hinzugefügte Items — **nicht** auf
`MetadataEdit` (manuelle Bearbeitung im UI). Dadurch überschreibt der Listener eine gerade von Hand
gesetzte `CommunityRating` **nicht** sofort. (Siehe `ListenerGate.IsAutomaticMetadataReason`.)

**Lücke.** Der periodische **Scheduled Task** und der **Post-Scan-Vollpass** (`RatingEnrichmentService.
RunAsync` → `EnrichmentRunner`) laufen **nicht** über dieses Gate. Sie wenden die externe Bewertung auf
**alle** Items der aktivierten Bibliotheken an. Eine manuell gesetzte Bewertung wird daher beim nächsten
Vollpass **doch wieder überschrieben** (die Pipeline schreibt, sobald `current != target`, siehe
`RatingPipeline.EvaluateFoundWriteAsync`). Das liegt im Kernmodell „das Plugin besitzt CommunityRating".

**Zu entscheiden.** Sollen **gesperrte Items** (Jellyfin `BaseItem.IsLocked`) generell übersprungen
werden — sowohl im Listener als auch im Vollpass —, damit der Nutzer eine Bewertung dauerhaft schützen
kann, indem er das Item sperrt? Das wäre der Jellyfin-native Weg.

Mögliche Umsetzung (späterer Schritt, z. B. Schritt 10):
- Vollpass: in `RatingEnrichmentService.BuildLibraryQuery` bzw. beim Einsammeln in `BuildWorkItems`
  Items mit `IsLocked == true` auslassen (oder ein `InternalItemsQuery`-Filter, falls vorhanden).
- Listener: in `EnrichItemAsync` (oder im Gate) `IsLocked` prüfen und überspringen.
- Alternativ ein eigenes Config-Flag „gesperrte Bewertungen respektieren".

Bis zur Entscheidung gilt: manuelle Werte bleiben nur bis zum nächsten Vollpass erhalten.
