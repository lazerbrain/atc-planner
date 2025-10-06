import { Component, OnDestroy, OnInit, ViewChild } from '@angular/core';
import {
  DrawerComponent,
  DrawerSelectEvent,
} from '@progress/kendo-angular-layout';
import { SVGIcon, menuIcon } from '@progress/kendo-svg-icons';
import { drawerRoutes } from './app.module';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { DrawerService } from './shared/services/drawer.service';

interface Item {
  text: string;
  svgIcon: SVGIcon;
  path: string;
  selected?: boolean;
}

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent implements OnInit, OnDestroy {
  @ViewChild('drawer') drawer!: DrawerComponent;

  title = 'ATC Roster';
  public expanded = false;
  public selected = 'Raspored';
  public items: Array<Item> = drawerRoutes;

  public menuSvg: SVGIcon = menuIcon;

  public currentDate: Date | null = null;
  public currentShift: string | null = null;

  private drawerSubscription!: Subscription;
  private scheduleSubscription!: Subscription;

  constructor(
    private router: Router,
    private route: ActivatedRoute,
    private drawerService: DrawerService
  ) {
    this.items[0].selected = true;
  }

  ngOnInit() {
    this.drawerSubscription = this.drawerService.drawerState$.subscribe(
      (state: boolean) => {
        this.expanded = state;

        if (this.drawer) {
          setTimeout(() => {
            this.drawer.toggle(state);
          });
        }
      }
    );

    this.scheduleSubscription = this.drawerService.scheduleInfo$.subscribe(
      (info) => {
        this.currentDate = info.date;
        this.currentShift = info.shift;
      }
    );
  }

  ngOnDestroy() {
    if (this.drawerSubscription) {
      this.drawerSubscription.unsubscribe();
    }

    if (this.scheduleSubscription) {
      this.scheduleSubscription.unsubscribe();
    }
  }

  public onSelect(ev: DrawerSelectEvent): void {
    this.router.navigate([`./${ev.item.path}`], { relativeTo: this.route });
    this.drawerService.setScheduleInfo(null, null);
  }

  toggleDrawer(): void {
    this.drawerService.toggleDrawer();
  }

  public formatDate(date: Date): string {
    return new Intl.DateTimeFormat('sr-RS', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
    }).format(date);
  }

  getFormattedScheduleInfo(): string {
    if (this.selected === 'Raspored' && this.currentDate && this.currentShift) {
      const formattedDate = new Intl.DateTimeFormat('sr-RS').format(
        this.currentDate
      );
      return ` (${formattedDate} - ${this.currentShift})`;
    }
    return '';
  }
}
