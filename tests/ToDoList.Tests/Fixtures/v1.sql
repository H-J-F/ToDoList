
        CREATE TABLE BookMetadata(Id INTEGER PRIMARY KEY CHECK(Id=1), Name TEXT NOT NULL, Title TEXT NOT NULL);
        CREATE TABLE Projects(Id TEXT PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE UNIQUE);
        CREATE TABLE Tasks(
          Id TEXT PRIMARY KEY, ProjectId TEXT REFERENCES Projects(Id), ContentJson TEXT NOT NULL,
          PlainText TEXT NOT NULL, Status INTEGER NOT NULL CHECK(Status IN (0,1,2)),
          CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, CompletedAt INTEGER,
          Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision>0),
          CHECK((Status=2 AND CompletedAt IS NOT NULL) OR (Status<>2 AND CompletedAt IS NULL)));
        CREATE TABLE TaskStateEvents(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
          FromStatus INTEGER NOT NULL CHECK(FromStatus IN (0,1,2)),
          ToStatus INTEGER NOT NULL CHECK(ToStatus IN (0,1,2)), OccurredAt INTEGER NOT NULL);
        CREATE TABLE BookSettings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        CREATE INDEX ix_created ON Tasks(CreatedAt,Id);
        CREATE INDEX ix_project_created ON Tasks(ProjectId,CreatedAt,Id);
        CREATE INDEX ix_open_created ON Tasks(CreatedAt,Id) WHERE Status<>2;
        CREATE INDEX ix_open_project_created ON Tasks(ProjectId,CreatedAt,Id) WHERE Status<>2;
        CREATE INDEX ix_done_created ON Tasks(CreatedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_project_created ON Tasks(ProjectId,CreatedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_completed ON Tasks(CompletedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_project_completed ON Tasks(ProjectId,CompletedAt,Id) WHERE Status=2;
        CREATE INDEX ix_events_task ON TaskStateEvents(TaskId,OccurredAt);
        