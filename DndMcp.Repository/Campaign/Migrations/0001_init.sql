-- 0001_init.sql: campaigns.db, Phase 6 (campaign core).
--
-- Applied by CampaignDbMigrator inside BEGIN IMMEDIATE after a VACUUM INTO pre-migration backup; the migrator sets
-- PRAGMA user_version and inserts the schema_migrations row itself (not this file). journal_mode=WAL is set once by
-- the migrator, outside the transaction. Every connection: "Foreign Keys=True;Default Timeout=5", then
-- PRAGMA busy_timeout = 5000.
--
-- Differences from research/04 §4 (each deliberate; PLAN §6 principles 4 and 8-11):
--  * JSON columns that must hold an object CHECK json_type(x) = 'object' (json_valid alone accepts arrays, numbers,
--    strings and null); live_log checks 'array'.
--  * entity_fts gains hidden_aliases (last column; bm25 weights 10, 8, 4, 1, 1, 2, 8). Only public/party aliases go into
--    `aliases`; restricted/author aliases go into `hidden_aliases`, which player column filters leave out.
--    entity_alias_au re-indexes when an alias or its visibility changes.
--  * change_log_no_replace: INSERT OR REPLACE onto an existing seq would otherwise overwrite a row with both
--    BEFORE UPDATE / BEFORE DELETE triggers in place (REPLACE's implicit delete fires no trigger while
--    recursive_triggers is off).
--  * change_log: `op` is mechanical (create | update | delete), which is all undo and as_of replay need; `action`
--    carries the semantic label (upsert, link, fact, reveal, tick, ...). `undo_of` names the batch a row reverses.
--  * Phase 7/8 tables (character_sheet, holding, currency_txn, award, encounter, combatant, combat_log, sim_run,
--    external_ref, calendar, timeline_event, world) and the columns that reference them are left to later
--    migrations: an FK to a table that does not exist yet is legal in SQLite but a trap.
--  * fact.visibility (research: default_visibility) defaults to 'restricted': a fact is seen by exactly the knowers
--    its knowledge rows name, plus the author.
--  * session: one live session per campaign (partial unique index).
--  * beat status carries met/cut (research: data.met / archived), so reachability reads one column.
--  * dice_roll is Phase 6 (dice_roll logging carry-forward); encounter_id arrives with Phase 7.
--  * entity.seq, fact.seq and dice_roll.seq are AUTOINCREMENT: an FTS rowid and an e:<n> / f:<n> handle must never be
--    reused, and a hard delete (undoing a create) would otherwise hand the next insert the same seq.
--  * knowledge uses two partial unique indexes (fact target, entity target) instead of research's coalesce() index:
--    each serves both uniqueness and lookup, and an UPSERT's ON CONFLICT target must repeat the index expression
--    exactly: ON CONFLICT(fact_id, knower_kind, coalesce(knower_id,'')) WHERE fact_id IS NOT NULL DO UPDATE ...
--  * entity_alias_au also fires on entity_id (an alias moved to another entity must leave the old FTS row), and tag_au
--    re-indexes on a tag rename.
--  * relation and objective visibility take public / party / author only: they have no knowledge rows, so
--    "restricted" could never be resolved for them.
--  * canon_status gains 'lean' (the DM's direction, not locked: One Piece open-questions.md).
--  * change_log gains other_entity_id (the second endpoint of a relation, the target of a knowledge row) so an
--    entity's history finds rows it is on either end of. change_log must never gain a UNIQUE constraint other than
--    seq: INSERT OR REPLACE on that key would rewrite a row past every trigger.
--  * Never use REPLACE / INSERT OR REPLACE on entity, fact, entity_alias, entity_tag or tag: REPLACE's implicit delete
--    fires no trigger (recursive_triggers is off unless the connection sets it), leaving a stale FTS row. Upserts are
--    INSERT ... ON CONFLICT(...) DO UPDATE. Connections also set Recursive Triggers=True as defence in depth.

CREATE TABLE schema_migrations (
  version INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_at TEXT NOT NULL
) STRICT;

CREATE TABLE app_state (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
) STRICT;

CREATE TABLE campaign (
  id TEXT PRIMARY KEY,
  slug TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('player','dm')),
  ruleset TEXT NOT NULL CHECK (ruleset IN ('2014','2024','mixed')),
  status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','hiatus','ended')),
  dm_name TEXT,
  my_character_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  party_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  current_location_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  current_ingame TEXT,
  settings TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(settings) AND json_type(settings) = 'object'),
  summary_md TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE entity (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  id TEXT NOT NULL UNIQUE,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('character','location','faction','item','lore','rule','homebrew',
        'quest','thread','question','secret','arc','beat','scene','session','event','handout','work','clock','front','note')),
  subtype TEXT,
  slug TEXT NOT NULL,
  code TEXT,
  name TEXT NOT NULL,
  summary TEXT NOT NULL DEFAULT '',
  body_md TEXT NOT NULL DEFAULT '',
  secret_md TEXT NOT NULL DEFAULT '',
  status TEXT,
  visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','restricted','author')),
  canon_status TEXT NOT NULL DEFAULT 'canon' CHECK (canon_status IN
        ('canon','played','ruled','lean','planned','proposed','accepted','struck','superseded')),
  confidence TEXT NOT NULL DEFAULT 'confirmed' CHECK (confidence IN ('confirmed','approximate','reconstructed','unverified')),
  parent_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  sort_key REAL,
  data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data) AND json_type(data) = 'object'),
  introduced_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  source TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT,
  UNIQUE (campaign_id, slug),
  CHECK (parent_id IS NULL OR parent_id <> id)
) STRICT;
CREATE UNIQUE INDEX ux_entity_code ON entity(campaign_id, code) WHERE code IS NOT NULL;
CREATE INDEX ix_entity_kind ON entity(campaign_id, kind, status) WHERE deleted_at IS NULL;
CREATE INDEX ix_entity_parent ON entity(parent_id) WHERE parent_id IS NOT NULL;

CREATE TABLE entity_alias (
  entity_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  alias TEXT NOT NULL COLLATE NOCASE,
  visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','restricted','author')),
  PRIMARY KEY (entity_id, alias)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_alias ON entity_alias(alias);

CREATE TABLE tag (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  name TEXT NOT NULL COLLATE NOCASE,
  UNIQUE (campaign_id, name)
) STRICT;

CREATE TABLE entity_tag (
  entity_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  tag_id TEXT NOT NULL REFERENCES tag(id) ON DELETE CASCADE,
  PRIMARY KEY (entity_id, tag_id)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_entity_tag_tag ON entity_tag(tag_id);

CREATE TABLE relation (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  from_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  rel TEXT NOT NULL,
  to_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  label TEXT,
  attitude INTEGER CHECK (attitude BETWEEN -100 AND 100),
  symmetric INTEGER NOT NULL DEFAULT 0 CHECK (symmetric IN (0,1)),
  visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','author')),
  status TEXT NOT NULL DEFAULT 'current' CHECK (status IN ('current','former','planned','rumored')),
  since_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  until_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data) AND json_type(data) = 'object'),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  CHECK (from_id <> to_id),
  UNIQUE (from_id, rel, to_id)
) STRICT;
CREATE INDEX ix_rel_to ON relation(to_id, rel);
CREATE INDEX ix_rel_campaign ON relation(campaign_id, rel);

-- The same being across campaigns (Keras). Never rendered for a non-author perspective: it is the Belmakor skill's
-- cross-campaign firewall. One row per pair, stored with a_id < b_id.
CREATE TABLE cross_link (
  a_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  b_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  note TEXT,
  created_at TEXT NOT NULL,
  PRIMARY KEY (a_id, b_id),
  CHECK (a_id < b_id)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_cross_link_b ON cross_link(b_id);

CREATE TABLE fact (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  id TEXT NOT NULL UNIQUE,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  code TEXT,
  statement TEXT NOT NULL,
  fact_type TEXT NOT NULL DEFAULT 'canon' CHECK (fact_type IN
        ('canon','ruling','secret','rumor','belief','clue','theory','meta')),
  truth TEXT NOT NULL DEFAULT 'true' CHECK (truth IN ('true','false','partial','unknown')),
  canon_status TEXT NOT NULL DEFAULT 'canon' CHECK (canon_status IN
        ('canon','played','ruled','lean','planned','proposed','accepted','struck','superseded')),
  confidence TEXT NOT NULL DEFAULT 'confirmed' CHECK (confidence IN ('confirmed','approximate','reconstructed','unverified')),
  visibility TEXT NOT NULL DEFAULT 'restricted' CHECK (visibility IN ('public','party','restricted','author')),
  gate TEXT CHECK (gate IS NULL OR (json_valid(gate) AND json_type(gate) = 'object')),
  established_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  source TEXT,
  superseded_by TEXT REFERENCES fact(id) ON DELETE SET NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT,
  CHECK (superseded_by IS NULL OR superseded_by <> id)
) STRICT;
CREATE UNIQUE INDEX ux_fact_code ON fact(campaign_id, code) WHERE code IS NOT NULL;
CREATE INDEX ix_fact_campaign ON fact(campaign_id, fact_type, canon_status) WHERE deleted_at IS NULL;

CREATE TABLE fact_link (
  fact_id TEXT NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
  entity_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  role TEXT NOT NULL DEFAULT 'about' CHECK (role IN ('about','source','location','clue_for','evidence','contradicts')),
  PRIMARY KEY (fact_id, entity_id, role)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_fact_link_entity ON fact_link(entity_id, role);

CREATE TABLE fact_dependency (
  fact_id TEXT NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
  depends_on TEXT NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
  PRIMARY KEY (fact_id, depends_on),
  CHECK (fact_id <> depends_on)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_fact_dependency_on ON fact_dependency(depends_on);

CREATE TABLE knowledge (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  fact_id TEXT REFERENCES fact(id) ON DELETE CASCADE,
  entity_id TEXT REFERENCES entity(id) ON DELETE CASCADE,
  knower_kind TEXT NOT NULL CHECK (knower_kind IN ('character','party','table','author','dm','public')),
  knower_id TEXT REFERENCES entity(id) ON DELETE CASCADE,
  state TEXT NOT NULL CHECK (state IN ('knows','suspects','believes','misbelieves','heard','met','aware',
        'unrecognized','unaware','forgot')),
  known_as TEXT,
  learned_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  learned_ingame TEXT,
  via_entity_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  how TEXT,
  note TEXT,
  valid_until_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  CHECK ((fact_id IS NULL) <> (entity_id IS NULL)),
  CHECK ((knower_kind = 'character') = (knower_id IS NOT NULL))
) STRICT;
CREATE UNIQUE INDEX ux_knowledge_fact ON knowledge(fact_id, knower_kind, coalesce(knower_id, '')) WHERE fact_id IS NOT NULL;
CREATE UNIQUE INDEX ux_knowledge_entity ON knowledge(entity_id, knower_kind, coalesce(knower_id, '')) WHERE entity_id IS NOT NULL;
CREATE INDEX ix_knowledge_knower ON knowledge(campaign_id, knower_kind, knower_id);
CREATE INDEX ix_knowledge_session ON knowledge(learned_session_id);

CREATE TABLE session (
  entity_id TEXT PRIMARY KEY REFERENCES entity(id) ON DELETE CASCADE,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  number INTEGER NOT NULL CHECK (number >= 0),
  status TEXT NOT NULL DEFAULT 'planned' CHECK (status IN ('planned','prepped','live','played','cancelled')),
  played_on TEXT,
  played_on_precision TEXT NOT NULL DEFAULT 'day' CHECK (played_on_precision IN ('day','month','approx','unknown')),
  ingame_start TEXT,
  ingame_end TEXT,
  arc_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  level_at_start INTEGER CHECK (level_at_start IS NULL OR level_at_start BETWEEN 1 AND 20),
  prep_md TEXT NOT NULL DEFAULT '',
  live_log TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(live_log) AND json_type(live_log) = 'array'),
  data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data) AND json_type(data) = 'object'),
  started_at TEXT,
  ended_at TEXT,
  UNIQUE (campaign_id, number)
) STRICT;
CREATE UNIQUE INDEX ux_session_live ON session(campaign_id) WHERE status = 'live';

CREATE TABLE session_attendance (
  session_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  character_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  present INTEGER NOT NULL DEFAULT 1 CHECK (present IN (0,1)),
  note TEXT,
  PRIMARY KEY (session_id, character_id)
) STRICT, WITHOUT ROWID;
CREATE INDEX ix_attendance_character ON session_attendance(character_id);

CREATE TABLE objective (
  id TEXT PRIMARY KEY,
  quest_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  ordinal REAL NOT NULL,
  text TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','done','failed','skipped','hidden')),
  progress INTEGER,
  progress_max INTEGER CHECK (progress_max IS NULL OR progress_max >= 1),
  visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','author')),
  resolved_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  updated_at TEXT NOT NULL,
  CHECK (progress IS NULL OR progress_max IS NULL OR progress BETWEEN 0 AND progress_max)
) STRICT;
CREATE INDEX ix_objective_quest ON objective(quest_id, ordinal);

CREATE TABLE clock (
  entity_id TEXT PRIMARY KEY REFERENCES entity(id) ON DELETE CASCADE,
  segments INTEGER NOT NULL CHECK (segments BETWEEN 1 AND 100),
  filled INTEGER NOT NULL DEFAULT 0,
  unit TEXT NOT NULL DEFAULT 'segment' CHECK (unit IN ('segment','round','hour','day','session','week')),
  front_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  shown_to_players INTEGER NOT NULL DEFAULT 0 CHECK (shown_to_players IN (0,1)),
  on_fill_md TEXT NOT NULL DEFAULT '',
  CHECK (filled BETWEEN 0 AND segments)
) STRICT;

CREATE TABLE beat_edge (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  from_beat_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  to_beat_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  mode TEXT NOT NULL CHECK (mode IN ('all_of','any_of')),
  CHECK (from_beat_id <> to_beat_id),
  UNIQUE (from_beat_id, to_beat_id)
) STRICT;
CREATE INDEX ix_beat_edge_to ON beat_edge(to_beat_id);

-- Rolls made at the table while a session is live. Append-only in practice; not in change_log (a roll is its own
-- record, and undoing a batch must never un-roll dice).
CREATE TABLE dice_roll (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  id TEXT NOT NULL UNIQUE,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  expression TEXT NOT NULL,
  label TEXT,
  total INTEGER NOT NULL,
  outcome INTEGER CHECK (outcome IS NULL OR outcome IN (0,1)),
  detail TEXT NOT NULL CHECK (json_valid(detail) AND json_type(detail) = 'object'),
  secret INTEGER NOT NULL DEFAULT 0 CHECK (secret IN (0,1)),
  at TEXT NOT NULL
) STRICT;
CREATE INDEX ix_roll_session ON dice_roll(session_id, seq);
CREATE INDEX ix_roll_campaign ON dice_roll(campaign_id, seq);

-- Append-only. campaign_id is deliberately NOT a foreign key: history outlives what it describes.
CREATE TABLE change_log (
  seq INTEGER PRIMARY KEY,
  campaign_id TEXT NOT NULL,
  at TEXT NOT NULL,
  session_id TEXT,
  actor TEXT NOT NULL,
  tool TEXT,
  batch_id TEXT NOT NULL,
  action TEXT NOT NULL,
  op TEXT NOT NULL CHECK (op IN ('create','update','delete')),
  target_table TEXT NOT NULL,
  target_id TEXT NOT NULL,
  entity_id TEXT,
  other_entity_id TEXT,
  field_path TEXT,
  old_value TEXT,
  new_value TEXT,
  reason TEXT,
  undo_of TEXT,
  CHECK ((op = 'update') = (field_path IS NOT NULL))
) STRICT;
CREATE INDEX ix_cl_entity ON change_log(entity_id, seq);
CREATE INDEX ix_cl_other_entity ON change_log(other_entity_id, seq) WHERE other_entity_id IS NOT NULL;
CREATE INDEX ix_cl_session ON change_log(campaign_id, session_id, seq);
CREATE INDEX ix_cl_at ON change_log(campaign_id, at);
CREATE INDEX ix_cl_batch ON change_log(batch_id);
CREATE INDEX ix_cl_undo ON change_log(undo_of) WHERE undo_of IS NOT NULL;
CREATE TRIGGER change_log_no_update BEFORE UPDATE ON change_log
  BEGIN SELECT RAISE(ABORT, 'change_log is append-only'); END;
CREATE TRIGGER change_log_no_delete BEFORE DELETE ON change_log
  BEGIN SELECT RAISE(ABORT, 'change_log is append-only'); END;
CREATE TRIGGER change_log_no_replace BEFORE INSERT ON change_log
  WHEN EXISTS (SELECT 1 FROM change_log WHERE seq = NEW.seq)
  BEGIN SELECT RAISE(ABORT, 'change_log is append-only'); END;

-- Full-text search. rowid = entity.seq / fact.seq (explicit INTEGER PRIMARY KEYs, so VACUUM cannot renumber them).
-- Column order is load-bearing: bm25(entity_fts, 10, 8, 4, 1, 1, 2, 8) weights name, aliases, summary, body, secret,
-- tags, hidden_aliases; Fts5Query.ColumnFiltered names the player-visible columns.
CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags, hidden_aliases,
  tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
CREATE VIRTUAL TABLE fact_fts USING fts5(statement,
  tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');

CREATE TRIGGER entity_fts_ai AFTER INSERT ON entity WHEN new.deleted_at IS NULL BEGIN
  INSERT INTO entity_fts(rowid, name, aliases, summary, body, secret, tags, hidden_aliases)
  VALUES (new.seq, new.name, '', new.summary, new.body_md, new.secret_md, '', '');
END;
CREATE TRIGGER entity_fts_au AFTER UPDATE OF name, summary, body_md, secret_md, deleted_at ON entity BEGIN
  DELETE FROM entity_fts WHERE rowid = old.seq;
  INSERT INTO entity_fts(rowid, name, aliases, summary, body, secret, tags, hidden_aliases)
  SELECT new.seq, new.name,
    coalesce((SELECT group_concat(alias, ' ') FROM entity_alias
              WHERE entity_id = new.id AND visibility IN ('public','party')), ''),
    new.summary, new.body_md, new.secret_md,
    coalesce((SELECT group_concat(t.name, ' ') FROM entity_tag et JOIN tag t ON t.id = et.tag_id
              WHERE et.entity_id = new.id), ''),
    coalesce((SELECT group_concat(alias, ' ') FROM entity_alias
              WHERE entity_id = new.id AND visibility NOT IN ('public','party')), '')
  WHERE new.deleted_at IS NULL;
END;
CREATE TRIGGER entity_fts_ad AFTER DELETE ON entity BEGIN
  DELETE FROM entity_fts WHERE rowid = old.seq;
END;
-- Alias and tag changes re-index by touching the owning entity (fires entity_fts_au). The touch goes through no
-- C# writer, so it never reaches change_log.
CREATE TRIGGER entity_alias_ai AFTER INSERT ON entity_alias BEGIN
  UPDATE entity SET name = name WHERE id = new.entity_id; END;
CREATE TRIGGER entity_alias_ad AFTER DELETE ON entity_alias BEGIN
  UPDATE entity SET name = name WHERE id = old.entity_id; END;
CREATE TRIGGER entity_alias_au AFTER UPDATE OF entity_id, alias, visibility ON entity_alias BEGIN
  UPDATE entity SET name = name WHERE id IN (old.entity_id, new.entity_id); END;
CREATE TRIGGER entity_tag_ai AFTER INSERT ON entity_tag BEGIN
  UPDATE entity SET name = name WHERE id = new.entity_id; END;
CREATE TRIGGER entity_tag_ad AFTER DELETE ON entity_tag BEGIN
  UPDATE entity SET name = name WHERE id = old.entity_id; END;
CREATE TRIGGER tag_au AFTER UPDATE OF name ON tag BEGIN
  UPDATE entity SET name = name WHERE id IN (SELECT entity_id FROM entity_tag WHERE tag_id = new.id); END;

CREATE TRIGGER fact_fts_ai AFTER INSERT ON fact WHEN new.deleted_at IS NULL BEGIN
  INSERT INTO fact_fts(rowid, statement) VALUES (new.seq, new.statement);
END;
CREATE TRIGGER fact_fts_au AFTER UPDATE OF statement, deleted_at ON fact BEGIN
  DELETE FROM fact_fts WHERE rowid = old.seq;
  INSERT INTO fact_fts(rowid, statement) SELECT new.seq, new.statement WHERE new.deleted_at IS NULL;
END;
CREATE TRIGGER fact_fts_ad AFTER DELETE ON fact BEGIN
  DELETE FROM fact_fts WHERE rowid = old.seq;
END;
