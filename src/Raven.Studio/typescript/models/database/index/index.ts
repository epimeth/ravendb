import appUrl = require("common/appUrl");

class index {
    static readonly priorityNormal: Raven.Client.Data.Indexes.IndexingPriority = "Normal";
    static readonly priorityIdle: Raven.Client.Data.Indexes.IndexingPriority = "Idle";
    static readonly priorityDisabled: Raven.Client.Data.Indexes.IndexingPriority = "Disabled";
    static readonly priorityErrored: Raven.Client.Data.Indexes.IndexingPriority = "Error";
    static readonly priorityIdleForced: Raven.Client.Data.Indexes.IndexingPriority = "Idle,Forced" as any;
    static readonly priorityDisabledForced: Raven.Client.Data.Indexes.IndexingPriority = "Disabled,Forced" as any;

    static readonly SideBySideIndexPrefix = "ReplacementOf/";
    static readonly TestIndexPrefix = "Test/";

    static readonly DefaultIndexGroupName = "Other";

    collections: { [index: string]: Raven.Client.Data.Indexes.CollectionStats; };
    collectionNames: Array<string>;
    createdTimestamp: string;
    entriesCount: number;
    errorsCount: number;
    id: number;
    isStale = ko.observable<boolean>(false);
    isInvalidIndex: boolean;
    isTestIndex: boolean;
    lastIndexingTime?: string;
    lastQueryingTime?: string;
    lockMode = ko.observable<Raven.Abstractions.Indexing.IndexLockMode>();
    mapAttempts: number;
    mapErrors: number;
    mapSuccesses: number;
    memory: Raven.Client.Data.Indexes.MemoryStats;
    name: string;
    priority = ko.observable<Raven.Client.Data.Indexes.IndexingPriority>();
    reduceAttempts?: number;
    reduceErrors?: number;
    reduceSuccesses?: number;
    type: Raven.Client.Data.Indexes.IndexType;

    filteredOut = ko.observable<boolean>(false); //UI only property
    badgeClass: KnockoutComputed<string>;
    editUrl: KnockoutComputed<string>;
    queryUrl: KnockoutComputed<string>;

    isNormalPriority: KnockoutComputed<boolean>;
    isDisabled: KnockoutComputed<boolean>;
    isIdle: KnockoutComputed<boolean>;
    isFaulty: KnockoutComputed<boolean>;
    pausedUntilRestart = ko.observable<boolean>();
    canBePaused: KnockoutComputed<boolean>;
    canBeResumed: KnockoutComputed<boolean>;

    constructor(dto: Raven.Client.Data.Indexes.IndexStats) {
        this.collections = dto.Collections;
        this.collectionNames = index.extractCollectionNames(dto.Collections);
        this.createdTimestamp = dto.CreatedTimestamp;
        this.entriesCount = dto.EntriesCount;
        this.errorsCount = dto.ErrorsCount;
        this.id = dto.Id;
        this.isStale(dto.IsStale);
        this.isInvalidIndex = dto.IsInvalidIndex;
        this.isTestIndex = dto.IsTestIndex;
        this.lastIndexingTime = dto.LastIndexingTime;
        this.lastQueryingTime = dto.LastQueryingTime;
        this.lockMode(dto.LockMode);
        this.mapAttempts = dto.MapAttempts;
        this.mapErrors = dto.MapErrors;
        this.mapSuccesses = dto.MapSuccesses;
        this.memory = dto.Memory;
        this.name = dto.Name;
        this.priority(dto.Priority);
        this.reduceAttempts = dto.ReduceAttempts;
        this.reduceErrors = dto.ReduceErrors;
        this.reduceSuccesses = dto.ReduceSuccesses;
        this.type = dto.Type;

        this.initializeObservables();
    }

    private getTypeForUI() {
        switch (this.type) {
            case "Map":
                return "Map";
            case "MapReduce":
                return "Map-Reduce";
            case "AutoMap":
                return "Auto Map";
            case "AutoMapReduce":
                return "Auto Map-Reduce";
            default:
                return this.type;
        }
    }

    private initializeObservables() {
        const urls = appUrl.forCurrentDatabase();
        this.queryUrl = urls.query(this.name);
        this.editUrl = urls.editIndex(this.name);

        this.isNormalPriority = ko.pureComputed((() => this.priority() === index.priorityNormal));
        this.isDisabled = ko.pureComputed(() => this.priority().contains(index.priorityDisabled));
        this.isIdle = ko.pureComputed(() => this.priority().contains(index.priorityIdle));
        this.canBePaused = ko.pureComputed(() => {
            const disabled = this.isDisabled();
            const paused = this.pausedUntilRestart();
            return !disabled && !paused;
        });
        this.canBeResumed = ko.pureComputed(() => {
            const disabled = this.isDisabled();
            const paused = this.pausedUntilRestart();
            return !disabled && paused;
        });

        this.isFaulty = ko.pureComputed(() => {
            const faultyType = "Faulty" as Raven.Client.Data.Indexes.IndexType;
            return this.type === faultyType;
        });

        this.badgeClass = ko.pureComputed(() => {
            const priority = this.priority();

            if (this.isFaulty()) {
                return "state-faulty";
            }

            if (this.pausedUntilRestart()) {
                return "state-paused";
            }
            
            if (priority.contains("Disabled")) {
                return "state-disabled";
            }

            if (priority.contains("Idle")) {
                return "state-idle";
            }

            if (priority.contains("Error")) {
                return "state-error";
            }

            return "state-normal";
        });
    }

    private static extractCollectionNames(collections: { [index: string]: Raven.Client.Data.Indexes.CollectionStats; }): string[] {
        const result = [] as Array<string>;

        for (let collection in collections) {
            if (collections.hasOwnProperty(collection)) {
                result.push(collection);
            }
        }
        return result;
    }

    getGroupName() {
        const collections = this.collectionNames;
        if (collections && collections.length) {
            return collections.slice(0).sort((l, r) => l.toLowerCase() > r.toLowerCase() ? 1 : -1).join(", ");
        } else {
            return index.DefaultIndexGroupName;
        }
    }

}

export = index; 