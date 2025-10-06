export interface RosterResponse {
  initialRoster: RosterData;
  optimizedRoster?: RosterData;
}

export interface RosterData {
  shiftStart: string;
  shiftEnd: string;
  roster: RosterEntry[];
  configurationSchedule: ConfigurationEntry[];
}

export interface RosterEntry {
  sifra: string;
  prezimeIme: string;
  smena: string;
  orm: string;
  redosled: number;
  par: string | null;
  datum: string;
  vremeStart: string;
  datumOd: string;
  datumDo: string;
  sektor: string;
  flag: string | null;
}

export interface ConfigurationEntry {
  datumOd: string;
  datumDo: string;
  oznakaKonfiguracije: string;
}

export type TimeSlot = string;
export type EmployeeId = string;

export interface TimeSlotDetails {
  value: string;
  flag: string | null;
  isIntermediateShift: boolean;
  highlighted?: boolean;
}

export interface PivotedRosterEntry {
  sifra: EmployeeId;
  prezimeIme: string;
  smena: string;
  orm: string;
  redosled: number;
  timeSlots: { [key: string]: TimeSlotDetails };
  selected?: boolean;
}

export interface PivotedConfigurationEntry {
  config: 'Configuration';
  [key: string]: string;
}
