/**
 * CNC Dispenser V2.1.5 - Calibration & Offset Manager
 * Мал лев страничен панел за прецизна калибрација на дизни и офсети
 */

(function() {
  'use strict';

  window.CNCApp = window.CNCApp || {};

  var Calibration = {
    active: false,
    activeDispenserId: 1,
    mode: 'nozzle_shift', // 'nozzle_shift' (Опција 1) или 'old_project' (Опција 2)
    directionLogic: 'move_nozzle', // 'move_nozzle' (природно) или 'show_drop' (инверзно)
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

      // Копче ✕ во заглавието на панелот
      var btnCloseHeader = document.getElementById('btnCloseCalibPanel');
      if (btnCloseHeader) {
        btnCloseHeader.onclick = function() {
          self.cancel();
        };
      }

      // Копче [ ✖ Откажи ]
      var btnCancel = document.getElementById('btnCalibCancel');
      if (btnCancel) {
        btnCancel.onclick = function() {
          self.cancel();
        };
      }

      // Копче [ ✅ Потврди (ОК) ]
      var btnApply = document.getElementById('btnCalibApply');
      if (btnApply) {
        btnApply.onclick = function() {
          self.applyAndSave();
        };
      }

      // Радио опции за режим
      var rMode1 = document.getElementById('calibModeNozzleShift');
      var rMode2 = document.getElementById('calibModeOldProject');

      if (rMode1) {
        rMode1.onchange = function() {
          if (rMode1.checked) {
            self.mode = 'nozzle_shift';
            self.updateUI();
          }
        };
      }
      if (rMode2) {
        rMode2.onchange = function() {
          if (rMode2.checked) {
            self.mode = 'old_project';
            self.updateUI();
          }
        };
      }

      // Паѓачко мени за логика на насока (за Опција 1)
      var selDir = document.getElementById('calibDirectionLogicSelect');
      if (selDir) {
        selDir.onchange = function() {
          self.directionLogic = selDir.value;
        };
      }

      // Избор на чекор (0.01, 0.05, 0.10, 0.50, 1.00)
      var selStep = document.getElementById('calibStepSelect');
      if (selStep) {
        selStep.onchange = function() {
          self.nudgeStep = parseFloat(selStep.value) || 0.05;
        };
      }

      // Копчиња за микро-џог во самиот панел
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
      var self = this;
      this.active = true;
      this.deltaX = 0.0;
      this.deltaY = 0.0;

      // Зачувај претходна состојба на сенчење (shadedDispensers)
      this.previousShadedSet = new Set(CNCApp.State.shadedDispensers || []);

      // Прикажи го панелот
      var panel = document.getElementById('calibrationSidePanel');
      if (panel) {
        panel.style.display = 'block';
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
      var firstDisp = CNCApp.State.machine.dispensers[0];
      this.selectDispenser(this.activeDispenserId || (firstDisp ? firstDisp.id : 1));
    },

    closeUI: function() {
      this.active = false;
      this.deltaX = 0.0;
      this.deltaY = 0.0;
      this.initialPointsMap.clear();

      // Сокриј го страничниот панел
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
            var conf = confirm('Имате незачувано поместување за Д' + self.activeDispenserId + '.\nСакате ли да преминете на друг диспензер без да го снимите?');
            if (!conf) return;
            self.revertToInitialPoints();
          }
          self.selectDispenser(d.id);
        };

        container.appendChild(btn);
      });
    },

    selectDispenser: function(dispId) {
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

      // ИЗОЛАЦИЈА: Другите бои стануваат полупроѕирни (shaded), а избраната свети 100%
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
        lblCurOffset.textContent = 'X: ' + (d.offsetX || 0).toFixed(3) + ' mm, Y: ' + (d.offsetY || 0).toFixed(3) + ' mm';
      }

      var lblShift = document.getElementById('calibLiveShiftVal');
      if (lblShift) {
        var sX = (this.deltaX > 0 ? '+' : '') + this.deltaX.toFixed(3);
        var sY = (this.deltaY > 0 ? '+' : '') + this.deltaY.toFixed(3);
        lblShift.textContent = 'ΔX: ' + sX + ' mm | ΔY: ' + sY + ' mm';
        lblShift.style.color = (this.deltaX !== 0 || this.deltaY !== 0) ? '#059669' : '#64748b';
      }

      var lblCount = document.getElementById('calibPointsCountBadge');
      if (lblCount) {
        lblCount.textContent = this.initialPointsMap.size + ' точки';
      }

      // Покажи/сокриј го паѓачкото мени за насока зависно од режимот
      var dirRow = document.getElementById('calibDirectionLogicRow');
      if (dirRow) {
        dirRow.style.display = (this.mode === 'nozzle_shift') ? 'block' : 'none';
      }

      // Објаснување во живо
      var hint = document.getElementById('calibModeExplainText');
      if (hint) {
        if (this.mode === 'nozzle_shift') {
          hint.innerHTML = '<strong>Опција 1:</strong> Поместете ги точките со стрелките на тастатурата или глувчето. Кога ќе кликнете ОК, точките <em>се враќаат на своето место врз сликата</em>, а се ажурира само офсетот на дизната.';
        } else {
          hint.innerHTML = '<strong>Опција 2:</strong> Поместете ги точките <em>точно врз сликата</em>. Кога ќе кликнете ОК, точките остануваат на сликата, а офсетот на дизната се компензира за машината да остане на истата позиција.';
        }
      }
    },

    // Се повикува од app.js / canvas.js кога се притиснати стрелки или се влече со глувче
    onPointsMoved: function(dx, dy) {
      if (!this.active) return;

      this.deltaX = parseFloat((this.deltaX + dx).toFixed(3));
      this.deltaY = parseFloat((this.deltaY + dy).toFixed(3));
      this.updateUI();
    },

    // Копчиња за микро-џог во самиот панел
    nudge: function(dx, dy) {
      if (!this.active || this.initialPointsMap.size === 0) return;

      var self = this;
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

    applyAndSave: function() {
      var d = (CNCApp.State.machine.dispensers || []).find(function(item) {
        return item.id === this.activeDispenserId;
      }.bind(this));

      if (!d) {
        this.closeUI();
        return;
      }

      var dx = this.deltaX;
      var dy = this.deltaY;

      if (dx === 0 && dy === 0) {
        alert('ℹ️ Нема направено никакво поместување.');
        this.closeUI();
        return;
      }

      CNCApp.State.snapshot();

      var oldOffsetX = d.offsetX || 0;
      var oldOffsetY = d.offsetY || 0;
      var finalNewOffsetX = oldOffsetX;
      var finalNewOffsetY = oldOffsetY;

      if (this.mode === 'nozzle_shift') {
        // ОПЦИЈА 1: Дизната бегала на машината. Точките на цртежот ОСТАНУВААТ НА СЛИКАТА!
        // Врати ги точките на нивните оригинални позиции врз сликата:
        this.initialPointsMap.forEach(function(initPos, idx) {
          var p = CNCApp.State.points[idx];
          if (p) {
            p.x = initPos.x;
            p.y = initPos.y;
            CNCApp.Table.updateRow(idx);
          }
        });

        // Пресметај нов офсет според логиката на насока:
        if (this.directionLogic === 'move_nozzle') {
          // Стрелка десно = дизна десно (+dx)
          finalNewOffsetX = parseFloat((oldOffsetX + dx).toFixed(3));
          finalNewOffsetY = parseFloat((oldOffsetY + dy).toFixed(3));
        } else {
          // Стрелка десно = капката паднала десно -> дизната се враќа лево (-dx)
          finalNewOffsetX = parseFloat((oldOffsetX - dx).toFixed(3));
          finalNewOffsetY = parseFloat((oldOffsetY - dy).toFixed(3));
        }

        d.offsetX = finalNewOffsetX;
        d.offsetY = finalNewOffsetY;

        this.notifySuccess(
          'Успешна калибрација на дизната за ' + d.name + '!\n\n' +
          '• Точките на цртежот останаа совршено на своето место врз сликата.\n' +
          '• Стар офсет: X=' + oldOffsetX.toFixed(3) + ', Y=' + oldOffsetY.toFixed(3) + ' mm\n' +
          '• Нов офсет: X=' + finalNewOffsetX.toFixed(3) + ', Y=' + finalNewOffsetY.toFixed(3) + ' mm'
        );

      } else {
        // ОПЦИЈА 2: Стар проект. Точките се погрешни и СЕГА СЕ ПОРАМНЕТИ СО СЛИКАТА.
        // Точките ОСТАНУВААТ на новата поместена позиција врз сликата!
        // А офсетот се компензира во спротивна насока (-dx, -dy) за физичката позиција на машината да остане идентична:
        finalNewOffsetX = parseFloat((oldOffsetX - dx).toFixed(3));
        finalNewOffsetY = parseFloat((oldOffsetY - dy).toFixed(3));

        d.offsetX = finalNewOffsetX;
        d.offsetY = finalNewOffsetY;

        this.notifySuccess(
          'Успешно усогласување на стар проект за ' + d.name + '!\n\n' +
          '• Точките остануваат на новата порамнета позиција врз сликата.\n' +
          '• Офсетот е компензиран: X=' + finalNewOffsetX.toFixed(3) + ', Y=' + finalNewOffsetY.toFixed(3) + ' mm.\n' +
          '• Машината ќе продолжи да нанесува на точното физичко место на парчето!'
        );
      }

      // Зачувај во локално складиште и синхронизирај со Mach3
      CNCApp.Storage.saveAutoSave();
      if (CNCApp.Machine && CNCApp.Machine.activeDriverId === 'mach3' && CNCApp.Mach3BridgeDriver && CNCApp.Mach3BridgeDriver.connected) {
        CNCApp.Mach3BridgeDriver.syncConfig();
      }

      this.closeUI();
    },

    notifySuccess: function(msg) {
      alert('✅ ' + msg);
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
