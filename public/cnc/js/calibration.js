/**
 * CNC Dispenser V2.1.6 - Calibration & Offset Manager
 * Широка, ниска и прегледна лента над табелата за прецизна калибрација на дизни и офсети
 */

(function() {
  'use strict';

  window.CNCApp = window.CNCApp || {};

  var Calibration = {
    active: false,
    activeDispenserId: 1,
    mode: 'nozzle_shift_natural', // 'nozzle_shift_natural', 'nozzle_shift_inverse', 'old_project'
    nudgeStep: 0.05, // mm
    deltaX: 0.0,
    deltaY: 0.0,
    initialPointsMap: new Map(), // index -> { x, y }
    previousShadedSet: null,

    init: function() {
      this.bindEvents();
    },

    bindEvents: function() {
      var self = this;

      // Копче над графикот за отворање / затворање
      var btnToggle = document.getElementById('btnOpenCalibration');
      if (btnToggle) {
        btnToggle.onclick = function() {
          if (self.active) {
            self.cancel();
          } else {
            self.open();
          }
        };
      }

      // Копче [ ✖ Затвори ]
      var btnCancel = document.getElementById('btnCalibCancel');
      if (btnCancel) {
        btnCancel.onclick = function() {
          self.cancel();
        };
      }

      // Копче [ ✔ Сними (ОК) ]
      var btnApply = document.getElementById('btnCalibApply');
      if (btnApply) {
        btnApply.onclick = function() {
          self.applyCurrentDispenser(false);
        };
      }

      // Копче [ 💾 Сними и Следен ➡️ ]
      var btnApplyNext = document.getElementById('btnCalibApplyNext');
      if (btnApplyNext) {
        btnApplyNext.onclick = function() {
          self.applyAndNext();
        };
      }

      // Паѓачко мени за режим
      var selMode = document.getElementById('calibModeSelect');
      if (selMode) {
        selMode.onchange = function() {
          self.mode = selMode.value;
          self.updateUI();
        };
      }

      // Избор на чекор (0.01, 0.05, 0.10, 0.50, 1.00)
      var selStep = document.getElementById('calibStepSelect');
      if (selStep) {
        selStep.onchange = function() {
          self.nudgeStep = parseFloat(selStep.value) || 0.05;
        };
      }

      // Копчиња за микро-стрелки во лентата
      var bUp = document.getElementById('btnCalibNudgeUp');
      var bDown = document.getElementById('btnCalibNudgeDown');
      var bLeft = document.getElementById('btnCalibNudgeLeft');
      var bRight = document.getElementById('btnCalibNudgeRight');

      if (bUp) bUp.onclick = function() { self.nudge(0, self.nudgeStep); };
      if (bDown) bDown.onclick = function() { self.nudge(0, -self.nudgeStep); };
      if (bLeft) bLeft.onclick = function() { self.nudge(-self.nudgeStep, 0); };
      if (bRight) bRight.onclick = function() { self.nudge(self.nudgeStep, 0); };

      // Ресетирање на моменталното поместување назад на нула
      var bResetShift = document.getElementById('btnCalibResetShift');
      if (bResetShift) {
        bResetShift.onclick = function() {
          self.revertToInitialPoints();
        };
      }
    },

    open: function() {
      this.active = true;
      this.deltaX = 0.0;
      this.deltaY = 0.0;

      // Зачувај претходна состојба на сенчење (shadedDispensers)
      this.previousShadedSet = new Set(CNCApp.State.shadedDispensers || []);

      // Прикажи ја лентата
      var panel = document.getElementById('calibrationSidePanel');
      if (panel) {
        panel.style.display = 'flex';
      }

      // Ажурирај го копчето над графикот во активна состојба
      var btnToggle = document.getElementById('btnOpenCalibration');
      if (btnToggle) {
        btnToggle.classList.add('active');
        btnToggle.innerHTML = '🎯 Калибрација: ВКЛ';
      }

      // Изгради копчиња за диспензери
      this.renderDispenserButtons();

      // Автоматски селектирај го првиот или веќе избраниот диспензер
      var firstDisp = (CNCApp.State.machine.dispensers || [])[0];
      var targetId = this.activeDispenserId || (firstDisp ? firstDisp.id : 1);
      this.selectDispenser(targetId);
    },

    closeUI: function() {
      this.active = false;
      this.deltaX = 0.0;
      this.deltaY = 0.0;
      this.initialPointsMap.clear();

      // Сокриј ја лентата
      var panel = document.getElementById('calibrationSidePanel');
      if (panel) {
        panel.style.display = 'none';
      }

      // Врати го копчето над графикот во нормална состојба
      var btnToggle = document.getElementById('btnOpenCalibration');
      if (btnToggle) {
        btnToggle.classList.remove('active');
        btnToggle.innerHTML = '🎯 Калибрација';
      }

      // Врати го претходното сенчење на боите
      if (this.previousShadedSet) {
        CNCApp.State.shadedDispensers = new Set(this.previousShadedSet);
        this.previousShadedSet = null;
      } else {
        CNCApp.State.shadedDispensers.clear();
      }

      if (typeof window.renderCanvasDispenserFilters === 'function') {
        window.renderCanvasDispenserFilters();
      }
      CNCApp.Canvas.render();
      CNCApp.Table.render();
    },

    cancel: function() {
      // Врати ги сите точки на оригиналните координати
      this.revertToInitialPoints();
      this.closeUI();
    },

    renderDispenserButtons: function() {
      var self = this;
      var container = document.getElementById('calibDispenserBtnGroup');
      if (!container) return;

      container.innerHTML = '';
      var dispensers = CNCApp.State.machine.dispensers || [];

      dispensers.forEach(function(d) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'calib-disp-btn' + (self.activeDispenserId === d.id ? ' active' : '');
        btn.innerHTML =
          '<span class="calib-disp-dot" style="background:' + (d.color || '#2563eb') + '"></span>' +
          '<strong>Д' + d.id + '</strong> ' +
          '<span style="font-size:10px;color:#64748b;">(' + d.name + ')</span>';

        btn.onclick = function() {
          if (self.deltaX !== 0 || self.deltaY !== 0) {
            var conf = confirm('Имате незачувано поместување за Д' + self.activeDispenserId + '.\nДали сакате да го снимите пред да преминете на Д' + d.id + '?');
            if (conf) {
              self.applyCurrentDispenser(true);
            } else {
              self.revertToInitialPoints();
            }
          }
          self.selectDispenser(d.id);
        };

        container.appendChild(btn);
      });
    },

    selectDispenser: function(dispId) {
      var self = this;
      this.activeDispenserId = dispId;
      this.deltaX = 0.0;
      this.deltaY = 0.0;
      this.initialPointsMap.clear();

      // Означи го активното копче
      var container = document.getElementById('calibDispenserBtnGroup');
      if (container) {
        var btns = container.querySelectorAll('.calib-disp-btn');
        var dispensers = CNCApp.State.machine.dispensers || [];
        btns.forEach(function(b, idx) {
          if (dispensers[idx] && dispensers[idx].id === dispId) {
            b.classList.add('active');
          } else {
            b.classList.remove('active');
          }
        });
      }

      // ИЗОЛАЦИЈА: Другите бои влегуваат во shadedDispensers (во Canvas се 100% скриени во режим на калибрација!)
      CNCApp.State.shadedDispensers.clear();
      (CNCApp.State.machine.dispensers || []).forEach(function(otherD) {
        if (otherD.id !== dispId) {
          CNCApp.State.shadedDispensers.add(otherD.id);
        }
      });

      // СЕЛЕКТИРАЈ ги сите точки од избраниот диспензер и сними ги почетните координати
      CNCApp.State.selectedIndices.clear();
      var count = 0;
      CNCApp.State.points.forEach(function(p, idx) {
        if (p.dispenser === dispId) {
          CNCApp.State.selectedIndices.add(idx);
          self.initialPointsMap.set(idx, { x: p.x, y: p.y });
          count++;
        }
      });

      if (typeof window.renderCanvasDispenserFilters === 'function') {
        window.renderCanvasDispenserFilters();
      }
      CNCApp.Canvas.render();
      CNCApp.Table.render();

      this.updateUI();
    },

    updateUI: function() {
      var d = (CNCApp.State.machine.dispensers || []).find(function(item) {
        return item.id === this.activeDispenserId;
      }.bind(this)) || { offsetX: 0, offsetY: 0, name: 'Боја ' + this.activeDispenserId };

      var lblCurOffset = document.getElementById('calibCurOffsetDisplay');
      if (lblCurOffset) {
        lblCurOffset.textContent = 'Офсет: X=' + (d.offsetX || 0).toFixed(3) + ', Y=' + (d.offsetY || 0).toFixed(3);
      }

      var lblShift = document.getElementById('calibLiveShiftVal');
      if (lblShift) {
        var sX = (this.deltaX > 0 ? '+' : '') + this.deltaX.toFixed(3);
        var sY = (this.deltaY > 0 ? '+' : '') + this.deltaY.toFixed(3);
        lblShift.textContent = 'ΔX: ' + sX + ' | ΔY: ' + sY;
        lblShift.className = 'calib-badge ' + ((this.deltaX !== 0 || this.deltaY !== 0) ? 'calib-badge-live' : 'calib-badge-neutral');
      }

      var lblCount = document.getElementById('calibPointsCountBadge');
      if (lblCount) {
        lblCount.textContent = this.initialPointsMap.size + ' точки';
      }
    },

    // Се повикува од app.js / canvas.js кога се притиснати стрелки или се влече со глувче
    onPointsMoved: function(dx, dy) {
      if (!this.active) return;

      this.deltaX = parseFloat((this.deltaX + dx).toFixed(3));
      this.deltaY = parseFloat((this.deltaY + dy).toFixed(3));
      this.updateUI();
    },

    // Копчиња за микро-џог во самата лента
    nudge: function(dx, dy) {
      if (!this.active || this.initialPointsMap.size === 0) return;

      this.initialPointsMap.forEach(function(initPos, idx) {
        var p = CNCApp.State.points[idx];
        if (p) {
          p.x = parseFloat((p.x + dx).toFixed(3));
          p.y = parseFloat((p.y + dy).toFixed(3));
          CNCApp.Table.updateRow(idx);
        }
      });

      this.deltaX = parseFloat((this.deltaX + dx).toFixed(3));
      this.deltaY = parseFloat((this.deltaY + dy).toFixed(3));

      CNCApp.Canvas.render();
      this.updateUI();
    },

    revertToInitialPoints: function() {
      this.initialPointsMap.forEach(function(initPos, idx) {
        var p = CNCApp.State.points[idx];
        if (p) {
          p.x = initPos.x;
          p.y = initPos.y;
          CNCApp.Table.updateRow(idx);
        }
      });

      this.deltaX = 0.0;
      this.deltaY = 0.0;
      CNCApp.Canvas.render();
      this.updateUI();
    },

    applyCurrentDispenser: function(stayInCalib) {
      var d = (CNCApp.State.machine.dispensers || []).find(function(item) {
        return item.id === this.activeDispenserId;
      }.bind(this));

      if (!d) {
        if (!stayInCalib) this.closeUI();
        return;
      }

      var dx = this.deltaX;
      var dy = this.deltaY;

      if (dx === 0 && dy === 0) {
        if (!stayInCalib) {
          alert('ℹ️ Нема направено никакво поместување за Д' + d.id + '.');
          this.closeUI();
        }
        return;
      }

      CNCApp.State.snapshot();

      var oldOffsetX = d.offsetX || 0;
      var oldOffsetY = d.offsetY || 0;
      var finalNewOffsetX = oldOffsetX;
      var finalNewOffsetY = oldOffsetY;

      var selModeEl = document.getElementById('calibModeSelect');
      var chosenMode = selModeEl ? selModeEl.value : this.mode;

      if (chosenMode === 'nozzle_shift_natural') {
        // ОПЦИЈА 1 Природно: Точките на цртежот се враќаат на своето место врз сликата!
        this.initialPointsMap.forEach(function(initPos, idx) {
          var p = CNCApp.State.points[idx];
          if (p) {
            p.x = initPos.x;
            p.y = initPos.y;
            CNCApp.Table.updateRow(idx);
          }
        });
        // Стрелка десно = дизна десно (+dx)
        finalNewOffsetX = parseFloat((oldOffsetX + dx).toFixed(3));
        finalNewOffsetY = parseFloat((oldOffsetY + dy).toFixed(3));
      } else if (chosenMode === 'nozzle_shift_inverse') {
        // ОПЦИЈА 1 Инверзно: Капката паднала таму, дизната оди спротивно
        this.initialPointsMap.forEach(function(initPos, idx) {
          var p = CNCApp.State.points[idx];
          if (p) {
            p.x = initPos.x;
            p.y = initPos.y;
            CNCApp.Table.updateRow(idx);
          }
        });
        finalNewOffsetX = parseFloat((oldOffsetX - dx).toFixed(3));
        finalNewOffsetY = parseFloat((oldOffsetY - dy).toFixed(3));
      } else {
        // ОПЦИЈА 2 Стар проект: Точките остануваат на новата порамнета позиција!
        // Офсетот се компензира во спротивна насока (-dx, -dy) за физичката позиција на машината да остане идентична:
        finalNewOffsetX = parseFloat((oldOffsetX - dx).toFixed(3));
        finalNewOffsetY = parseFloat((oldOffsetY - dy).toFixed(3));
      }

      d.offsetX = finalNewOffsetX;
      d.offsetY = finalNewOffsetY;

      // Ажурирај ја мапата со нови позиции
      var self = this;
      this.initialPointsMap.forEach(function(_, idx) {
        var p = CNCApp.State.points[idx];
        if (p) {
          self.initialPointsMap.set(idx, { x: p.x, y: p.y });
        }
      });

      this.deltaX = 0.0;
      this.deltaY = 0.0;

      // Зачувај во локално складиште и синхронизирај со Mach3
      CNCApp.Storage.saveAutoSave();
      if (CNCApp.Machine && CNCApp.Machine.activeDriverId === 'mach3' && CNCApp.Mach3BridgeDriver && CNCApp.Mach3BridgeDriver.connected) {
        CNCApp.Mach3BridgeDriver.syncConfig();
      }

      this.updateUI();
      CNCApp.Canvas.render();

      if (!stayInCalib) {
        alert(
          '✅ Успешна калибрација за Д' + d.id + ' (' + d.name + ')!\n\n' +
          '• Стар офсет: X=' + oldOffsetX.toFixed(3) + ', Y=' + oldOffsetY.toFixed(3) + ' mm\n' +
          '• Нов офсет: X=' + finalNewOffsetX.toFixed(3) + ', Y=' + finalNewOffsetY.toFixed(3) + ' mm'
        );
        this.closeUI();
      }
    },

    applyAndNext: function() {
      // Прво сними го тековниот диспензер доколку има поместување
      if (this.deltaX !== 0 || this.deltaY !== 0) {
        this.applyCurrentDispenser(true);
      }

      // Најди го следниот диспензер
      var dispensers = CNCApp.State.machine.dispensers || [];
      if (dispensers.length === 0) return;

      var curIdx = 0;
      for (var i = 0; i < dispensers.length; i++) {
        if (dispensers[i].id === this.activeDispenserId) {
          curIdx = i;
          break;
        }
      }

      var nextIdx = (curIdx + 1) % dispensers.length;
      var nextDisp = dispensers[nextIdx];

      // Селектирај го следниот диспензер без затворање на панелот!
      this.selectDispenser(nextDisp.id);
    }
  };

  CNCApp.Calibration = Calibration;

  // Иницијализација кога DOM е подготвен
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function() {
      Calibration.init();
    });
  } else {
    Calibration.init();
  }
})();
