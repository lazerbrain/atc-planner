import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

interface ScheduleInfo {
  date: Date | null;
  shift: string | null;
}

@Injectable({
  providedIn: 'root',
})
export class DrawerService {
  private drawerState = new BehaviorSubject<boolean>(false);
  public drawerState$ = this.drawerState.asObservable();

  private scheduleInfo = new BehaviorSubject<ScheduleInfo>({
    date: null,
    shift: null,
  });
  scheduleInfo$ = this.scheduleInfo.asObservable();

  setDrawerState(expanded: boolean): void {
    this.drawerState.next(expanded);
  }

  toggleDrawer(): void {
    this.drawerState.next(!this.drawerState.value);
  }

  setScheduleInfo(date: Date | null, shift: string | null): void {
    this.scheduleInfo.next({ date, shift });
  }
}
