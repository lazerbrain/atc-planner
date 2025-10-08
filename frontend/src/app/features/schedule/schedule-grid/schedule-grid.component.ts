import {
  ChangeDetectorRef,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { GridComponent } from '@progress/kendo-angular-grid';
import {
  PivotedConfigurationEntry,
  PivotedRosterEntry,
} from 'src/app/models/roster-response.model';

@Component({
  selector: 'app-schedule-grid',
  templateUrl: './schedule-grid.component.html',
  styleUrls: ['./schedule-grid.component.css'],
})
export class ScheduleGridComponent implements OnChanges {
  @Input() data: (PivotedRosterEntry | PivotedConfigurationEntry)[] = [];
  @Input() timeSlots: string[] = [];
  @Input() slotShortages: { [key: string]: number } = {};

  @ViewChild(GridComponent) grid!: GridComponent;
  @Output() selectedEmployeesChange = new EventEmitter<string[]>();

  formattedTimeSlots: { start: string; end: string }[] = [];
  gridHeight!: string;
  selectAllChecked: boolean = true;

  private exceptedSectors: string[] = ['SS', 'SUP', 'FMP', 'FD', 'FPC']; // za proveru parova iskljuci ove pozicije

  constructor(private cdr: ChangeDetectorRef) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['data']) {
      this.rearrangeData();
      this.initializeSelection();
      this.checkSectorPairs();
    }

    if (changes['timeSlots']) {
      this.formatTimeSlots();
    }
  }

  private initializeSelection() {
    this.data = this.data.map((item) => {
      if (!this.isConfigurationRow(item)) {
        const rosterEntry = item as PivotedRosterEntry;
        rosterEntry.selected = !['SS', 'SUP', 'FMP', 'KPL', 'SPL'].includes(
          rosterEntry.orm
        );
      }
      return item;
    });
    this.updateSelectAllState();
    this.emitSelectedEmployees();
  }

  private updateSelectAllState() {
    this.selectAllChecked = this.data.every(
      (item) =>
        this.isConfigurationRow(item) || (item as PivotedRosterEntry).selected
    );
  }

  isRosterEntry(
    item: PivotedRosterEntry | PivotedConfigurationEntry
  ): item is PivotedRosterEntry {
    return (item as PivotedRosterEntry).sifra !== undefined;
  }

  isSelectable(
    dataItem: PivotedRosterEntry | PivotedConfigurationEntry
  ): boolean {
    return this.isRosterEntry(dataItem);
  }

  onSelectionChange(dataItem: PivotedRosterEntry) {
    dataItem.selected = !dataItem.selected;
    this.updateSelectAllState();
    this.emitSelectedEmployees();
  }

  onSelectionAllChange() {
    this.selectAllChecked = !this.selectAllChecked;
    this.data = this.data.map((item) => {
      if (this.isSelectable(item)) {
        (item as PivotedRosterEntry).selected = this.selectAllChecked;
      }
      return item;
    });
    this.emitSelectedEmployees();
  }

  private emitSelectedEmployees() {
    const selectedEmployees = this.data
      .filter(
        (item): item is PivotedRosterEntry =>
          this.isRosterEntry(item) && item.selected === true
      )
      .map((item) => item.sifra);
    this.selectedEmployeesChange.emit(selectedEmployees);
  }

  getGridHeight(): string {
    const rowHeight = 30; // Fixed row height (can be adjusted)
    const numberOfRows = this.data.length;
    const headerHeight = 35; // Fixed header height

    // Calculate total grid height based on number of rows
    const totalHeight = headerHeight + numberOfRows * rowHeight;

    return `${totalHeight}px`;
  }

  private rearrangeData() {
    // Separate out configuration entries and roster entries
    const configurationEntries = this.data.filter(
      (item): item is PivotedConfigurationEntry => !this.isRosterEntry(item)
    );
    const rosterEntries = this.data.filter((item): item is PivotedRosterEntry =>
      this.isRosterEntry(item)
    );

    // Reassign the data with roster entries first, and configuration entries at the end
    this.data = [...rosterEntries, ...configurationEntries];
  }

  private formatTimeSlots() {
    this.formattedTimeSlots = this.timeSlots.map((slot, index) => {
      // The correct split should be on the last dash, separating the two full datetime strings
      const lastDashIndex = slot.lastIndexOf('-');
      const startFull = slot.substring(0, lastDashIndex); // Extract everything before the last dash
      const endFull = slot.substring(lastDashIndex + 1); // Extract everything after the last dash

      // Extract and format times (hh:mm) from the full datetime strings
      const start = this.extractTime(startFull);
      const end = this.extractTime(endFull);

      return { start, end };
    });
  }

  private extractTime(dateTimeString: string): string {
    // Split the datetime string to extract the time part (T separates date and time)
    const parts = dateTimeString.split('T');
    if (parts.length === 2) {
      const timePart = parts[1].substring(0, 5); // Extract only 'hh:mm'
      return timePart;
    }

    console.error('Unexpected date format:', dateTimeString);
    return '00:00'; // Fallback in case of incorrect format
  }

  checkSectorPairs() {
    this.timeSlots.forEach((timeSlot) => {
      const sectorGroups: { [key: string]: string[] } = {};

      // Групишемо секторе по првa два слова, искључујући изузете секторе
      this.data.forEach((item) => {
        if (this.isRosterEntry(item)) {
          const sector = item.timeSlots[timeSlot]?.value;
          if (sector && sector.length >= 2 && !this.isExceptedSector(sector)) {
            const sectorBase = sector.substring(0, 2);
            if (!sectorGroups[sectorBase]) {
              sectorGroups[sectorBase] = [];
            }
            sectorGroups[sectorBase].push(sector);
          }
        }
      });

      // Проверавамо да ли свака група има и 'E' и 'P' варијанту
      Object.entries(sectorGroups).forEach(([sectorBase, sectors]) => {
        const hasE = sectors.some((s) => s.length > 2 && s[2] === 'E');
        const hasP = sectors.some((s) => s.length > 2 && s[2] === 'P');

        if (!hasE || !hasP) {
          // Означавамо све секторе у овој групи као истакнуте
          this.data.forEach((item) => {
            if (this.isRosterEntry(item)) {
              const itemSector = item.timeSlots[timeSlot]?.value;
              if (
                itemSector &&
                itemSector.startsWith(sectorBase) &&
                !this.isExceptedSector(itemSector)
              ) {
                if (item.timeSlots[timeSlot]) {
                  item.timeSlots[timeSlot].highlighted = true;
                }
              }
            }
          });
        }
      });
    });
  }

  private isExceptedSector(sector: string): boolean {
    return this.exceptedSectors.some((exceptedSector) =>
      sector.includes(exceptedSector)
    );
  }

  isSectorHighlighted(
    dataItem: PivotedRosterEntry | PivotedConfigurationEntry,
    timeSlot: string
  ): boolean {
    return (
      this.isRosterEntry(dataItem) &&
      dataItem.timeSlots[timeSlot]?.highlighted === true &&
      !this.isExceptedSector(dataItem.timeSlots[timeSlot]?.value || '')
    );
  }

  isConfigurationRow(dataItem: any): boolean {
    return dataItem.config === 'Configuration';
  }

  isShortageSlot(timeSlot: string): boolean {
    return this.slotShortages[timeSlot] > 0;
  }

  getShortageCount(timeSlot: string): number {
    return this.slotShortages[timeSlot] || 0;
  }

  getTooltip(dataItem: any, timeSlot: string): string {
    if (this.isConfigurationRow(dataItem)) {
      const shortage = this.getShortageCount(timeSlot);
      if (shortage > 0) {
        return `Manjak izvršilaca: ${shortage}`;
      }
    } else if (this.isSectorHighlighted(dataItem, timeSlot)) {
      const sector = dataItem.timeSlots[timeSlot]?.value;
      return `Nedostaje par (E/P) za sektor ${sector.substring(0, 2)}`;
    }
    return '';
  }

  isShaded(
    dataItem: PivotedRosterEntry | PivotedConfigurationEntry,
    timeSlot: string
  ): boolean {
    if (this.isRosterEntry(dataItem)) {
      // Ako timeslot uopšte ne postoji za zaposlenog, to znači da je međusmena
      if (!dataItem.timeSlots || !dataItem.timeSlots[timeSlot]) {
        return true; // zasivi jer je međusmena
      }

      const slotData = dataItem.timeSlots[timeSlot];
      const isShaded =
        slotData &&
        (slotData.flag === 'S' ||
          (slotData.value === '' && slotData.flag !== null));

      return isShaded;
    }
    return false;
  }

  calculateWorkingHours(
    dataItem: PivotedRosterEntry | PivotedConfigurationEntry
  ): number {
    if (!this.isRosterEntry(dataItem)) {
      return 0;
    }

    let workingSlots = 0;
    let nonOperationalSectors = ['SS', 'SUP', 'BRF', 'SBY', 'FMP'];
    this.timeSlots.forEach((timeSlot) => {
      // Računamo samo slotove koji imaju value i nemaju flag='S'
      if (
        dataItem.timeSlots[timeSlot]?.value &&
        dataItem.timeSlots[timeSlot]?.flag !== 'S' &&
        !nonOperationalSectors.includes(dataItem.timeSlots[timeSlot]?.value)
      ) {
        workingSlots++;
      }
    });

    // Svaki slot je 30 minuta, pa delimo sa 2 da dobijemo sate
    return workingSlots / 2;
  }

  getWorkingHoursTooltip(dataItem: PivotedRosterEntry): string {
    const regularHours = this.calculateWorkingHours(dataItem);
    const sectors = new Set();

    this.timeSlots.forEach((timeSlot) => {
      const value = dataItem.timeSlots[timeSlot]?.value;
      if (value && !dataItem.timeSlots[timeSlot]?.flag) {
        sectors.add(value);
      }
    });

    return `Ukupno sati: ${regularHours}
  Sektori: ${Array.from(sectors).join(', ')}`;
  }
}
