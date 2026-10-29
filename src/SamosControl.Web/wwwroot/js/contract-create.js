(function(){
  var tpl=document.getElementById("TemplateId"), sel=document.getElementById("SellerId"), cust=document.getElementById("CustomerId");
  function isPartner(){var o=tpl.options[tpl.selectedIndex];return o&&o.dataset.partner==="1";}
  function fill(){
    var o=sel.options[sel.selectedIndex], d=o?o.dataset:{}, set=function(k,v){var e=document.querySelector("#partnerSummary [data-f="+k+"]");if(e)e.textContent=v||"—";};
    set("name",d.name);set("doc",d.doc||"não informado");set("phone",d.phone||"não informado");set("pix",d.pix||"não informada");
    set("impl",d.impl?d.impl+"%":"");set("mens",d.mens?d.mens+"%":"");set("months",d.name?(d.months?"primeiros "+d.months+" mês(es) de cada cliente":"enquanto o cliente estiver ativo e adimplente"):"");
  }
  function apply(){
    var p=isPartner();
    document.querySelectorAll(".client-only").forEach(function(e){e.classList.toggle("d-none",p);});
    document.querySelectorAll(".partner-only").forEach(function(e){e.classList.toggle("d-none",!p);});
    cust.required=!p; sel.required=p; if(p)fill();
  }
  tpl.addEventListener("change",apply); sel.addEventListener("change",fill); apply();
})();
