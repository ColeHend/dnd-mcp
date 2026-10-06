-- 0002_characters_combat.sql: campaigns.db, Phase 7 (characters + combat).
--
-- Applied by CampaignDbMigrator as 0001 was: a pre-migrate-v2 backup of an existing file, BEGIN IMMEDIATE, then the
-- migrator's PRAGMA user_version = 2 and schema_migrations row. No Phase 6 table is rebuilt: rebuilding entity would drop
-- its FTS triggers. Phase 6 tables only gain columns (ALTER TABLE ... ADD COLUMN appends, so the catalogue and row
-- records list them last), and only on tables that are never replayed (dice_roll): a column added to a logged table
-- would break as_of reads of delete snapshots taken before it existed (CampaignRows.FromValues).
-- A process of an earlier build (0.6.0) refuses this file once it is migrated: its newest migration is 0001.
--
-- Logged through ChangeRecorder (CampaignTables.All): character_sheet, holding, currency_txn, award. Their keys are TEXT
-- and their CHECKs span one column each, because undo reverses one logged column at a time. Every column Phase 8's
-- imports need is here now for the same as_of reason.
-- Not logged (CampaignTables.NotLogged): encounter, combatant, combat_log. HP ticks stay out of change_log (PLAN 7); the
-- combat log is the tracker's audit trail, and the end-of-combat write-back is one logged batch. No logged table has a
-- foreign key into them, so no FK action on them changes a logged row without history.
-- Not here: sim_run (balance_simulate stays read-only), clock.encounter_id (the as_of trap above), external_ref
-- (Phase 8), research's character_sheet.is_mine (campaign.my_character_id already says it).

CREATE TABLE character_sheet (
  entity_id TEXT PRIMARY KEY REFERENCES entity(id) ON DELETE CASCADE,
  player TEXT,
  ruleset TEXT CHECK (ruleset IS NULL OR ruleset IN ('2014','2024')),
  species TEXT,
  lineage TEXT,
  background TEXT,
  size TEXT,
  classes TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(classes) AND json_type(classes) = 'array'),
  level INTEGER CHECK (level IS NULL OR level BETWEEN 1 AND 20),
  xp INTEGER CHECK (xp IS NULL OR xp >= 0),
  abilities TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(abilities) AND json_type(abilities) = 'object'),
  saves TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(saves) AND json_type(saves) = 'object'),
  skills TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(skills) AND json_type(skills) = 'object'),
  ac INTEGER CHECK (ac IS NULL OR ac BETWEEN 0 AND 50),
  max_hp INTEGER CHECK (max_hp IS NULL OR max_hp BETWEEN 1 AND 5000),
  max_hp_reduction INTEGER NOT NULL DEFAULT 0 CHECK (max_hp_reduction >= 0),
  hp INTEGER CHECK (hp IS NULL OR hp >= 0),
  temp_hp INTEGER NOT NULL DEFAULT 0 CHECK (temp_hp >= 0),
  speed INTEGER CHECK (speed IS NULL OR speed >= 0),
  movement TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(movement) AND json_type(movement) = 'object'),
  senses TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(senses) AND json_type(senses) = 'object'),
  initiative_bonus INTEGER,
  passive_perception INTEGER,
  spell_save_dc INTEGER,
  spell_attack INTEGER,
  defenses TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(defenses) AND json_type(defenses) = 'object'),
  hit_dice TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(hit_dice) AND json_type(hit_dice) = 'object'),
  spell_slots TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(spell_slots) AND json_type(spell_slots) = 'object'),
  resources TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(resources) AND json_type(resources) = 'object'),
  conditions TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(conditions) AND json_type(conditions) = 'array'),
  concentration TEXT CHECK (concentration IS NULL OR (json_valid(concentration) AND json_type(concentration) = 'object')),
  death_saves TEXT NOT NULL DEFAULT '{"successes":0,"failures":0,"stable":false}' CHECK (json_valid(death_saves) AND json_type(death_saves) = 'object'),
  exhaustion INTEGER NOT NULL DEFAULT 0 CHECK (exhaustion BETWEEN 0 AND 6),
  inspiration INTEGER NOT NULL DEFAULT 0 CHECK (inspiration IN (0,1)),
  feats TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(feats) AND json_type(feats) = 'array'),
  features TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(features) AND json_type(features) = 'array'),
  spells TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(spells) AND json_type(spells) = 'array'),
  languages TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(languages) AND json_type(languages) = 'array'),
  sim_profile TEXT CHECK (sim_profile IS NULL OR (json_valid(sim_profile) AND json_type(sim_profile) = 'object')),
  notes_md TEXT NOT NULL DEFAULT '',
  sheet_source TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE holding (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  holder_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  item_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  srd_ref TEXT,
  quantity REAL NOT NULL DEFAULT 1 CHECK (quantity >= 0),
  equipped INTEGER NOT NULL DEFAULT 0 CHECK (equipped IN (0,1)),
  attuned INTEGER NOT NULL DEFAULT 0 CHECK (attuned IN (0,1)),
  charges TEXT CHECK (charges IS NULL OR (json_valid(charges) AND json_type(charges) = 'object')),
  acquired_session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
) STRICT;
CREATE INDEX ix_holding_holder ON holding(holder_id);
CREATE INDEX ix_holding_item ON holding(item_id) WHERE item_id IS NOT NULL;

CREATE TABLE currency_txn (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  holder_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  cp INTEGER NOT NULL DEFAULT 0,
  sp INTEGER NOT NULL DEFAULT 0,
  ep INTEGER NOT NULL DEFAULT 0,
  gp INTEGER NOT NULL DEFAULT 0,
  pp INTEGER NOT NULL DEFAULT 0,
  note TEXT NOT NULL,
  created_at TEXT NOT NULL
) STRICT;
CREATE INDEX ix_currency_holder ON currency_txn(holder_id, created_at);

CREATE TABLE award (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  recipient_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  kind TEXT NOT NULL CHECK (kind IN ('xp','milestone','level','boon','inspiration','renown')),
  amount INTEGER,
  note TEXT,
  source TEXT,
  created_at TEXT NOT NULL
) STRICT;
CREATE INDEX ix_award_recipient ON award(recipient_id, created_at);
CREATE INDEX ix_award_session ON award(session_id) WHERE session_id IS NOT NULL;

CREATE TABLE encounter (
  id TEXT PRIMARY KEY,
  campaign_id TEXT NOT NULL REFERENCES campaign(id) ON DELETE CASCADE,
  scene_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  session_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  ruleset TEXT NOT NULL CHECK (ruleset IN ('2014','2024')),
  status TEXT NOT NULL DEFAULT 'planned' CHECK (status IN ('planned','active','paused','ended')),
  round INTEGER NOT NULL DEFAULT 0 CHECK (round >= 0),
  turn_combatant_id TEXT,
  lair INTEGER NOT NULL DEFAULT 0 CHECK (lair IN (0,1)),
  data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data) AND json_type(data) = 'object'),
  notes_md TEXT NOT NULL DEFAULT '',
  outcome_md TEXT,
  writeback_batch_id TEXT,
  started_at TEXT,
  ended_at TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_encounter_active ON encounter(campaign_id) WHERE status = 'active';
CREATE INDEX ix_encounter_campaign ON encounter(campaign_id, status);
CREATE INDEX ix_encounter_session ON encounter(session_id) WHERE session_id IS NOT NULL;

CREATE TABLE combatant (
  id TEXT PRIMARY KEY,
  encounter_id TEXT NOT NULL REFERENCES encounter(id) ON DELETE CASCADE,
  entity_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  side TEXT NOT NULL CHECK (side IN ('party','ally','enemy','neutral')),
  init_group TEXT,
  srd_ref TEXT,
  statblock TEXT CHECK (statblock IS NULL OR (json_valid(statblock) AND json_type(statblock) = 'object')),
  initiative REAL,
  init_bonus INTEGER NOT NULL DEFAULT 0,
  ac INTEGER,
  max_hp INTEGER CHECK (max_hp IS NULL OR max_hp >= 1),
  max_hp_reduction INTEGER NOT NULL DEFAULT 0 CHECK (max_hp_reduction >= 0),
  hp INTEGER CHECK (hp IS NULL OR hp >= 0),
  temp_hp INTEGER NOT NULL DEFAULT 0 CHECK (temp_hp >= 0),
  damage_taken INTEGER NOT NULL DEFAULT 0 CHECK (damage_taken >= 0),
  conditions TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(conditions) AND json_type(conditions) = 'array'),
  concentration TEXT CHECK (concentration IS NULL OR (json_valid(concentration) AND json_type(concentration) = 'object')),
  death_saves TEXT NOT NULL DEFAULT '{"successes":0,"failures":0,"stable":false}' CHECK (json_valid(death_saves) AND json_type(death_saves) = 'object'),
  makes_death_saves INTEGER NOT NULL DEFAULT 0 CHECK (makes_death_saves IN (0,1)),
  exhaustion INTEGER NOT NULL DEFAULT 0 CHECK (exhaustion BETWEEN 0 AND 6),
  legendary TEXT CHECK (legendary IS NULL OR (json_valid(legendary) AND json_type(legendary) = 'object')),
  resources TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(resources) AND json_type(resources) = 'object'),
  sheet_snapshot TEXT CHECK (sheet_snapshot IS NULL OR (json_valid(sheet_snapshot) AND json_type(sheet_snapshot) = 'object')),
  reaction_used INTEGER NOT NULL DEFAULT 0 CHECK (reaction_used IN (0,1)),
  surprised INTEGER NOT NULL DEFAULT 0 CHECK (surprised IN (0,1)),
  hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0,1)),
  defeated INTEGER NOT NULL DEFAULT 0 CHECK (defeated IN (0,1)),
  dead INTEGER NOT NULL DEFAULT 0 CHECK (dead IN (0,1)),
  removed INTEGER NOT NULL DEFAULT 0 CHECK (removed IN (0,1)),
  order_key REAL NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  CHECK (hp IS NULL OR max_hp IS NULL OR hp <= max_hp)
) STRICT;
CREATE INDEX ix_combatant_encounter ON combatant(encounter_id, order_key);
CREATE INDEX ix_combatant_entity ON combatant(entity_id) WHERE entity_id IS NOT NULL;

CREATE TABLE combat_log (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  encounter_id TEXT NOT NULL REFERENCES encounter(id) ON DELETE CASCADE,
  round INTEGER NOT NULL CHECK (round >= 0),
  turn_combatant_id TEXT,
  actor_id TEXT REFERENCES combatant(id) ON DELETE SET NULL,
  target_id TEXT REFERENCES combatant(id) ON DELETE SET NULL,
  kind TEXT NOT NULL CHECK (kind IN ('start','add','remove','initiative','turn','damage','heal','temp_hp','condition','concentration','save','death_save','legendary','resource','defeat','note','end','import')),
  amount INTEGER,
  detail TEXT CHECK (detail IS NULL OR (json_valid(detail) AND json_type(detail) = 'object')),
  roll_id TEXT REFERENCES dice_roll(id) ON DELETE SET NULL,
  at TEXT NOT NULL
) STRICT;
CREATE INDEX ix_combat_log_encounter ON combat_log(encounter_id, seq);

ALTER TABLE dice_roll ADD COLUMN encounter_id TEXT REFERENCES encounter(id) ON DELETE SET NULL;
CREATE INDEX ix_roll_encounter ON dice_roll(encounter_id, seq) WHERE encounter_id IS NOT NULL;
