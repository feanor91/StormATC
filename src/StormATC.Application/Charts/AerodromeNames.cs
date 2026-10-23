namespace StormATC.Application.Charts;

/// <summary>The names of the French aerodromes, by ICAO code, to label their charts.</summary>
public static class AerodromeNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LFAB"] = "Dieppe - Saint-Aubin",
        ["LFAC"] = "Calais - Dunkerque",
        ["LFAD"] = "Compiègne - Margny",
        ["LFAE"] = "Eu - Mers - Le Tréport",
        ["LFAF"] = "Laon - Chambry",
        ["LFAG"] = "Estrées-Mons",
        ["LFAI"] = "Nangis Les Loges",
        ["LFAJ"] = "Argentan",
        ["LFAK"] = "Dunkerque - Les Moëres",
        ["LFAL"] = "Flèche - Thorée-les-Pins",
        ["LFAM"] = "Berck-sur-Mer",
        ["LFAO"] = "Bagnoles-de-l'Orne - Couterne",
        ["LFAP"] = "Rethel - Perthes",
        ["LFAQ"] = "International Amiens Henry Potez",
        ["LFAR"] = "Montdidier",
        ["LFAS"] = "Falaise-Mont d'Eraines",
        ["LFAT"] = "Le Touquet-Elizabeth II",
        ["LFAU"] = "Vauville",
        ["LFAV"] = "Valenciennes - Denain",
        ["LFAW"] = "Villerupt",
        ["LFAX"] = "Mortagne-au-Perche",
        ["LFAY"] = "Amiens - Glisy",
        ["LFBA"] = "Agen-La Garenne",
        ["LFBC"] = "Cazaux",
        ["LFBD"] = "Bordeaux - Mérignac",
        ["LFBE"] = "Bergerac Dordogne Périgord",
        ["LFBF"] = "Toulouse-Francazal",
        ["LFBG"] = "Cognac-Châteaubernard",
        ["LFBH"] = "Rochelle - île de Ré",
        ["LFBI"] = "Poitiers-Biard",
        ["LFBJ"] = "Saint-Junien",
        ["LFBK"] = "Montluçon Guéret",
        ["LFBL"] = "Limoges-Bellegarde",
        ["LFBM"] = "Mont-de-Marsan",
        ["LFBN"] = "Niort - Marais Poitevin",
        ["LFBO"] = "Toulouse-Blagnac",
        ["LFBP"] = "Pau-Pyrénées",
        ["LFBR"] = "Muret - Lherm",
        ["LFBS"] = "Grands Lacs",
        ["LFBT"] = "Tarbes-Lourdes-Pyrénées",
        ["LFBU"] = "international Angoulême-Cognac",
        ["LFBX"] = "Périgueux-Bassillac",
        ["LFBY"] = "Dax - Seyresse",
        ["LFBZ"] = "Biarritz Pays Basque",
        ["LFCA"] = "Châtellerault - Targé",
        ["LFCB"] = "Bagnères-de-Luchon",
        ["LFCC"] = "Cahors - Lalbenque",
        ["LFCD"] = "Andernos-les-Bains",
        ["LFCE"] = "Guéret - Saint-Laurent",
        ["LFCF"] = "Figeac-Livernon",
        ["LFCG"] = "Saint-Girons - Antichan",
        ["LFCH"] = "Arcachon - La Teste-de-Buch",
        ["LFCI"] = "Albi-Le Séquestre",
        ["LFCJ"] = "Jonzac - Neulles",
        ["LFCK"] = "Castres-Mazamet",
        ["LFCL"] = "Toulouse - Lasbordes",
        ["LFCM"] = "Millau-Larzac",
        ["LFCN"] = "Nogaro",
        ["LFCO"] = "Oloron - Herrère",
        ["LFCP"] = "Pons - Avy",
        ["LFCQ"] = "Graulhet - Montdragon",
        ["LFCR"] = "Rodez-Aveyron",
        ["LFCS"] = "Bordeaux - Léognan - Saucats",
        ["LFCT"] = "Thouars",
        ["LFCU"] = "Ussel - Thalamy",
        ["LFCV"] = "Villefranche-de-Rouergue",
        ["LFCW"] = "Villeneuve-sur-Lot",
        ["LFCX"] = "Castelsarrasin - Moissac",
        ["LFCY"] = "Royan - Médis",
        ["LFCZ"] = "Mimizan",
        ["LFDA"] = "Aire-sur-l'Adour",
        ["LFDB"] = "Montauban",
        ["LFDC"] = "Montendre - Marcillac",
        ["LFDE"] = "Égletons",
        ["LFDF"] = "Sainte-Foy-la-Grande",
        ["LFDG"] = "Gaillac - Lisle-sur-Tarn",
        ["LFDH"] = "Auch - Gers",
        ["LFDI"] = "Libourne - Artigues-de-Lussac",
        ["LFDJ"] = "Pamiers - Les Pujols",
        ["LFDK"] = "Soulac-sur-Mer",
        ["LFDL"] = "Loudun",
        ["LFDM"] = "Marmande - Virazeil",
        ["LFDN"] = "Rochefort - Charente-Maritime",
        ["LFDP"] = "Saint-Pierre-d'Oléron",
        ["LFDQ"] = "Castelnau-Magnoac",
        ["LFDR"] = "Réole - Floudès",
        ["LFDS"] = "Sarlat - Domme",
        ["LFDT"] = "Tarbes - Laloubère",
        ["LFDU"] = "Lesparre - Saint-Laurent-Médoc",
        ["LFDV"] = "Couhé - Vérac",
        ["LFDW"] = "Chauvigny",
        ["LFDX"] = "Fumel - Montayral",
        ["LFDY"] = "Bordeaux - Yvrac",
        ["LFDZ"] = "Condat-sur-Vézère",
        ["LFEA"] = "Belle-Île-en-Mer",
        ["LFEB"] = "Dinan - Trélivan",
        ["LFEC"] = "Ouessant",
        ["LFED"] = "Pontivy",
        ["LFEF"] = "Amboise - Dierre",
        ["LFEG"] = "Argenton-sur-Creuse",
        ["LFEH"] = "Aubigny-sur-Nère",
        ["LFEI"] = "Briare - Châtillon",
        ["LFEJ"] = "Châteauroux - Villers",
        ["LFEK"] = "Issoudun - Le Fay",
        ["LFEL"] = "Blanc",
        ["LFEM"] = "Montargis - Vimory",
        ["LFEN"] = "Tours - Sorigny",
        ["LFEP"] = "Pouilly - Maconge",
        ["LFEQ"] = "Quiberon",
        ["LFER"] = "Redon - Bains-sur-Oust",
        ["LFES"] = "Guiscriff - Scaër",
        ["LFET"] = "Til-Châtel",
        ["LFEU"] = "Bar-le-Duc - Les Hauts-de-Chée",
        ["LFEV"] = "Gray - Saint-Adrien",
        ["LFEW"] = "Saulieu - Liernais",
        ["LFEX"] = "Nancy - Azelot",
        ["LFEY"] = "Île-d'Yeu",
        ["LFEZ"] = "Nancy - Malzéville",
        ["LFFB"] = "Buno-Bonnevaux",
        ["LFFC"] = "Mantes - Chérence",
        ["LFFD"] = "Saint-André-de-l'Eure",
        ["LFFE"] = "Enghien - Moisselles",
        ["LFFG"] = "Ferté-Gaucher",
        ["LFFH"] = "Château-Thierry - Belleau",
        ["LFFI"] = "Ancenis",
        ["LFFJ"] = "Joinville - Mussey",
        ["LFFK"] = "Fontenay-le-Comte",
        ["LFFL"] = "Bailleau-Armenonville",
        ["LFFN"] = "Brienne-le-Château",
        ["LFFO"] = "Beauvoir - Fromentine",
        ["LFFP"] = "Pithiviers",
        ["LFFQ"] = "Ferté-Alais",
        ["LFFR"] = "Bar-sur-Seine",
        ["LFFT"] = "Neufchâteau",
        ["LFFU"] = "Châteauneuf-sur-Cher",
        ["LFFV"] = "Vierzon - Méreau",
        ["LFFW"] = "Montaigu - Saint-Georges",
        ["LFFX"] = "Tournus - Cuisery",
        ["LFFY"] = "Étrépagny",
        ["LFFZ"] = "Sézanne - Saint-Remy",
        ["LFGA"] = "Colmar - Houssen",
        ["LFGB"] = "Mulhouse - Habsheim",
        ["LFGC"] = "Strasbourg-Neuhof",
        ["LFGE"] = "Avallon",
        ["LFGF"] = "Beaune - Challanges",
        ["LFGG"] = "Belfort - Chaux",
        ["LFGH"] = "Cosne-sur-Loire",
        ["LFGI"] = "Dijon - Darois",
        ["LFGJ"] = "Dole-Jura",
        ["LFGK"] = "Joigny",
        ["LFGL"] = "Lons-le-Saunier - Courlaoux",
        ["LFGM"] = "Montceau-les-Mines - Pouilloux",
        ["LFGN"] = "Paray-le-Monial",
        ["LFGO"] = "Pont-sur-Yonne",
        ["LFGP"] = "Saint-Florentin - Chéu",
        ["LFGQ"] = "Semur-en-Auxois",
        ["LFGR"] = "Doncourt-lès-Conflans",
        ["LFGS"] = "Longuyon - Villette",
        ["LFGT"] = "Sarrebourg - Buhl",
        ["LFGU"] = "Sarreguemines - Neunkirch",
        ["LFGW"] = "Verdun - Sommedieue",
        ["LFGX"] = "Champagnole - Crotenay",
        ["LFGY"] = "Saint-Dié - Remomeix",
        ["LFGZ"] = "Nuits-Saint-Georges",
        ["LFHA"] = "Issoire - Le Broc",
        ["LFHC"] = "Pérouges - Meximieux",
        ["LFHD"] = "Pierrelatte",
        ["LFHE"] = "Romans - Saint-Paul",
        ["LFHF"] = "Ruoms",
        ["LFHG"] = "Saint-Chamond - L'Horme",
        ["LFHH"] = "Vienne - Reventin",
        ["LFHI"] = "Morestel",
        ["LFHJ"] = "Lyon - Corbas",
        ["LFHL"] = "Langogne - Lespéron",
        ["LFHM"] = "Megève",
        ["LFHN"] = "Bellegarde - Vouvray",
        ["LFHO"] = "Aubenas Ardèche méridionale",
        ["LFHP"] = "Puy - Loudes",
        ["LFHQ"] = "Saint-Flour - Coltines",
        ["LFHR"] = "Brioude - Beaumont",
        ["LFHS"] = "Bourg - Ceyzériat",
        ["LFHT"] = "Ambert - Le Poyet",
        ["LFHU"] = "Alpe d'Huez",
        ["LFHV"] = "Villefranche - Tarare",
        ["LFHW"] = "Belleville - Villié-Morgon",
        ["LFHX"] = "Lapalisse - Périgny",
        ["LFHY"] = "Moulins - Montbeugny",
        ["LFIB"] = "Belvès - Saint-Pardoux",
        ["LFID"] = "Condom - Valence-sur-Baïse",
        ["LFIF"] = "Saint-Affrique - Belmont",
        ["LFIG"] = "Cassagnes-Bégonhès",
        ["LFIH"] = "Chalais",
        ["LFIK"] = "Ribérac - Saint-Aulaye",
        ["LFIL"] = "Rion-des-Landes",
        ["LFIM"] = "Saint-Gaudens - Montréjeau",
        ["LFIP"] = "Peyresourde-Balestas",
        ["LFIR"] = "Revel - Montgey",
        ["LFIS"] = "Saint-Inglevert - Les deux Caps",
        ["LFIT"] = "Toulouse - Bourg-Saint-Bernard",
        ["LFIV"] = "Vendays-Montalivet",
        ["LFIX"] = "Itxassou",
        ["LFIY"] = "Saint-Jean-d'Angély - Saint-Denis-du-Pin",
        ["LFJA"] = "Chaumont - Semoutiers",
        ["LFJB"] = "Mauléon",
        ["LFJC"] = "Clamecy",
        ["LFJD"] = "Corlier",
        ["LFJE"] = "Motte-Chalancon",
        ["LFJF"] = "Aubenasson",
        ["LFJH"] = "Cazères - Palaminy",
        ["LFJI"] = "Marennes",
        ["LFJL"] = "Metz-Nancy-Lorraine",
        ["LFJR"] = "Angers Loire",
        ["LFJS"] = "Soissons - Courmelles",
        ["LFJT"] = "Tours - Le Louroux",
        ["LFJU"] = "Lurcy-Lévis",
        ["LFJV"] = "Lasclaveries",
        ["LFJY"] = "Chambley",
        ["LFKA"] = "Albertville",
        ["LFKB"] = "Bastia Poretta",
        ["LFKC"] = "Calvi-Sainte-Catherine",
        ["LFKD"] = "Sollières-Sardières",
        ["LFKE"] = "Saint-Jean-en-Royans",
        ["LFKF"] = "Figari Sud Corse",
        ["LFKG"] = "Ghisonaccia Alzitone",
        ["LFKH"] = "Saint-Jean-d'Avelanne",
        ["LFKJ"] = "Ajaccio Napoléon Bonaparte",
        ["LFKL"] = "Lyon - Brindas",
        ["LFKM"] = "Saint-Galmier",
        ["LFKO"] = "Propriano",
        ["LFKP"] = "Tour-du-Pin - Cessieu",
        ["LFKR"] = "Saint-Rémy-de-Maurienne",
        ["LFKS"] = "Base aérienne de Solenzara",
        ["LFKT"] = "Corte",
        ["LFKX"] = "Méribel",
        ["LFKY"] = "Belley - Peyrieu",
        ["LFLA"] = "Auxerre - Branches",
        ["LFLB"] = "Chambéry - Savoie",
        ["LFLC"] = "Clermont-Ferrand Auvergne",
        ["LFLD"] = "Bourges",
        ["LFLE"] = "Chambéry - Challes-les-Eaux",
        ["LFLG"] = "Grenoble - Le Versoud",
        ["LFLH"] = "Chalon - Champforgeuil",
        ["LFLI"] = "Annemasse",
        ["LFLJ"] = "Courchevel",
        ["LFLL"] = "Lyon-Saint-Exupéry",
        ["LFLM"] = "Mâcon - Charnay",
        ["LFLN"] = "Saint-Yan",
        ["LFLO"] = "Roanne-Renaison",
        ["LFLP"] = "Annecy Haute-Savoie Mont-Blanc",
        ["LFLQ"] = "Montélimar - Ancône",
        ["LFLR"] = "Saint-Rambert-d'Albon",
        ["LFLS"] = "International de Grenoble-Isère",
        ["LFLT"] = "Montluçon - Domérat",
        ["LFLU"] = "Valence-Chabeuil",
        ["LFLV"] = "Vichy — Charmeil",
        ["LFLW"] = "Aurillac",
        ["LFLX"] = "Châteauroux - Centre",
        ["LFLY"] = "Lyon-Bron",
        ["LFLZ"] = "Feurs - Chambéon",
        ["LFMA"] = "Aix - Les Milles",
        ["LFMC"] = "Luc - Le Cannet",
        ["LFMD"] = "Cannes - Mandelieu",
        ["LFME"] = "Nîmes-Courbessac",
        ["LFMF"] = "Fayence - Tourrettes",
        ["LFMG"] = "Montagne Noire",
        ["LFMH"] = "Saint-Étienne-Loire",
        ["LFMI"] = "Base aérienne d'Istres - Le Tubé",
        ["LFMK"] = "Carcassonne Salvaza",
        ["LFML"] = "Marseille Provence",
        ["LFMN"] = "Nice-Côte d'Azur",
        ["LFMO"] = "Base aérienne d'Orange Caritat",
        ["LFMP"] = "Perpignan-Rivesaltes",
        ["LFMQ"] = "Castellet",
        ["LFMR"] = "Barcelonnette - Saint-Pons",
        ["LFMS"] = "Alès Cévennes",
        ["LFMT"] = "Montpellier-Méditerranée",
        ["LFMU"] = "Béziers-Cap d'Agde",
        ["LFMV"] = "Avignon - Caumont",
        ["LFMW"] = "Castelnaudary - Villeneuve",
        ["LFMX"] = "Château-Arnoux-Saint-Auban",
        ["LFMY"] = "Base aérienne de Salon-de-Provence",
        ["LFMZ"] = "Lézignan-Corbières",
        ["LFNA"] = "Gap - Tallard",
        ["LFNB"] = "Mende - Brenoux",
        ["LFNC"] = "Mont-Dauphin - Saint-Crépin",
        ["LFNE"] = "Salon - Eyguières",
        ["LFNF"] = "Vinon",
        ["LFNG"] = "Montpellier - Candillargues",
        ["LFNH"] = "Carpentras",
        ["LFNJ"] = "Aspres-sur-Buëch",
        ["LFNL"] = "Saint-Martin-de-Londres",
        ["LFNN"] = "Narbonne-Vinassan",
        ["LFNO"] = "Florac - Sainte-Enimie",
        ["LFNQ"] = "Mont-Louis La Quillane",
        ["LFNR"] = "Berre - La Fare",
        ["LFNS"] = "Sisteron-Thèze",
        ["LFNT"] = "Avignon - Pujaut",
        ["LFNU"] = "Uzès",
        ["LFNV"] = "Valréas - Visan",
        ["LFNW"] = "Puivert",
        ["LFNX"] = "Bédarieux - La Tour-sur-Orb",
        ["LFNY"] = "Saint-Étienne-en-Dévoluy",
        ["LFNZ"] = "Mazet de Romanin",
        ["LFOA"] = "Base aérienne d'Avord",
        ["LFOB"] = "Paris Beauvais",
        ["LFOC"] = "Châteaudun",
        ["LFOD"] = "Saumur - Saint-Florent",
        ["LFOE"] = "Évreux-Fauville",
        ["LFOF"] = "Alençon - Valframbert",
        ["LFOG"] = "Flers - Saint-Paul",
        ["LFOH"] = "Havre-Octeville",
        ["LFOI"] = "Abbeville",
        ["LFOJ"] = "Base aérienne d'Orléans - Bricy",
        ["LFOK"] = "Paris Châlons Vatry",
        ["LFOL"] = "Aigle - Saint-Michel",
        ["LFOM"] = "Lessay",
        ["LFON"] = "Dreux - Vernouillet",
        ["LFOO"] = "Sables-d'Olonne - Talmont",
        ["LFOP"] = "Rouen Vallée de Seine",
        ["LFOQ"] = "Blois - Le Breuil",
        ["LFOR"] = "Chartres",
        ["LFOS"] = "Saint-Valery - Vittefleur",
        ["LFOT"] = "Tours Val de Loire",
        ["LFOU"] = "Cholet - Le Pontreau",
        ["LFOV"] = "Laval - Entrammes",
        ["LFOW"] = "Saint-Quentin - Roupy",
        ["LFOX"] = "Étampes - Mondésir",
        ["LFOY"] = "Havre - Saint-Romain-de-Colbosc",
        ["LFOZ"] = "Orléans Loire Valley",
        ["LFPA"] = "Persan - Beaumont",
        ["LFPB"] = "Paris-Le Bourget",
        ["LFPC"] = "Creil",
        ["LFPD"] = "Bernay - Saint-Martin",
        ["LFPE"] = "Meaux - Esbly",
        ["LFPF"] = "Beynes - Thiverval",
        ["LFPG"] = "Paris-Charles-de-Gaulle",
        ["LFPH"] = "Chelles - Le Pin",
        ["LFPI"] = "Paris - Issy-les-Moulineaux",
        ["LFPK"] = "Coulommiers - Voisins",
        ["LFPL"] = "Lognes-Émerainville",
        ["LFPM"] = "Melun-Villaroche",
        ["LFPN"] = "Paris-Saclay Versailles",
        ["LFPO"] = "Paris-Orly",
        ["LFPP"] = "Plessis-Belleville",
        ["LFPQ"] = "Fontenay-Trésigny",
        ["LFPT"] = "Paris- Pontoise",
        ["LFPU"] = "Moret - Épisy",
        ["LFPV"] = "Base aérienne de Vélizy-Villacoublay",
        ["LFPX"] = "Chavenay - Villepreux",
        ["LFPZ"] = "Saint-Cyr-l'École",
        ["LFQA"] = "Reims - Prunay",
        ["LFQB"] = "Troyes - Barberey",
        ["LFQC"] = "Lunéville - Croismare",
        ["LFQD"] = "Arras - Roclincourt",
        ["LFQE"] = "Base aérienne d'Étain-Rouvres",
        ["LFQF"] = "Autun-Bellevue",
        ["LFQG"] = "Nevers-Fourchambault",
        ["LFQH"] = "Châtillon-sur-Seine",
        ["LFQI"] = "Base aérienne de Cambrai-Épinoy",
        ["LFQJ"] = "Maubeuge - Élesmes",
        ["LFQK"] = "Châlons - Écury-sur-Coole",
        ["LFQL"] = "Lens - Bénifontaine",
        ["LFQM"] = "Besançon-La Vèze",
        ["LFQN"] = "Saint-Omer - Wizernes",
        ["LFQO"] = "Lille - Marcq-en-Barœul",
        ["LFQQ"] = "Lille - Lesquin",
        ["LFQS"] = "Vitry-en-Artois",
        ["LFQT"] = "Merville - Calonne",
        ["LFQU"] = "Sarre-Union",
        ["LFQV"] = "Charleville-Mézières - Belval",
        ["LFQW"] = "Vesoul - Frotey",
        ["LFQX"] = "Juvancourt",
        ["LFQY"] = "Saverne-Steinbourg",
        ["LFQZ"] = "Dieuze - Guéblange",
        ["LFRB"] = "Brest-Bretagne",
        ["LFRC"] = "Cherbourg - Manche",
        ["LFRD"] = "Dinard Saint-Malo",
        ["LFRE"] = "Baule-Escoublac",
        ["LFRF"] = "Granville-Mont-Saint-Michel",
        ["LFRG"] = "Deauville - Normandie",
        ["LFRH"] = "Lorient Bretagne Sud",
        ["LFRI"] = "Roche-sur-Yon - Les Ajoncs",
        ["LFRJ"] = "Base d'aéronautique navale de Landivisiau",
        ["LFRK"] = "Caen - Carpiquet",
        ["LFRL"] = "Base d'aéronautique navale de Lanvéoc-Poulmic",
        ["LFRM"] = "Le Mans-Arnage",
        ["LFRN"] = "Rennes - Saint-Jacques",
        ["LFRO"] = "Lannion",
        ["LFRP"] = "Ploërmel - Loyat",
        ["LFRQ"] = "Quimper Cornouaille",
        ["LFRS"] = "Nantes Atlantique",
        ["LFRT"] = "Saint-Brieuc Armor",
        ["LFRU"] = "Morlaix Ploujean",
        ["LFRV"] = "Vannes - Meucon",
        ["LFRW"] = "Avranches - Le Val-Saint-Père",
        ["LFRZ"] = "Saint-Nazaire - Montoir",
        ["LFSA"] = "Besançon - Thise",
        ["LFSB"] = "Européen de Bâle-Mulhouse-Fribourg (EuroAirPort)",
        ["LFSD"] = "Dijon-Bourgogne",
        ["LFSE"] = "Épinal - Dogneville",
        ["LFSG"] = "Épinal-Mirecourt",
        ["LFSH"] = "Haguenau",
        ["LFSI"] = "Saint-Dizier-Robinson",
        ["LFSJ"] = "Sedan - Douzy",
        ["LFSK"] = "Vitry-le-François - Vauclerc",
        ["LFSL"] = "Brive-Vallée de Dordogne",
        ["LFSM"] = "Montbéliard - Courcelles",
        ["LFSN"] = "Nancy-Essey",
        ["LFSO"] = "Nancy-Ochey",
        ["LFSP"] = "Pontarlier",
        ["LFSS"] = "Grand-Fougeray - Saint-Sulpice-des-Landes",
        ["LFST"] = "Strasbourg-Entzheim",
        ["LFSU"] = "Langres - Rolampont",
        ["LFSV"] = "Pont-Saint-Vincent",
        ["LFSW"] = "Épernay - Plivot",
        ["LFSX"] = "Luxeuil-Saint Sauveur",
        ["LFSY"] = "Cessey",
        ["LFTA"] = "Tranche-sur-Mer",
        ["LFTB"] = "Hydroaérodrome de Marignane - Berre",
        ["LFTF"] = "Cuers - Pierrefeu",
        ["LFTH"] = "Toulon-Hyères",
        ["LFTL"] = "Cannes - Quai du large",
        ["LFTM"] = "Serres - La Bâtie-Montsaléon",
        ["LFTN"] = "Grand'Combe",
        ["LFTP"] = "Puimoisson",
        ["LFTQ"] = "Châteaubriant - Pouancé",
        ["LFTW"] = "Nîmes-Garons",
        ["LFTZ"] = "Saint-Tropez",
        ["LFVM"] = "Miquelon",
        ["LFVP"] = "Saint-Pierre - Pointe-Blanche",
        ["LFXA"] = "Ambérieu",
        ["LFXB"] = "Saintes - Thénac",
        ["LFXH"] = "Base aérienne du Valdahon",
        ["LFXM"] = "Mourmelon",
        ["LFXQ"] = "Base aérienne de Coëtquidan",
        ["LFXU"] = "Mureaux",
        ["LFYG"] = "Cambrai-Niergnies",
        ["LFYR"] = "Romorantin-Pruniers",
        ["LFYS"] = "Sainte-Léocadie",
    };

    /// <summary>The name of the aerodrome, or null when it is not known.</summary>
    /// <param name="icao">The ICAO code.</param>
    public static string? Find(string? icao) =>
        icao is not null && Names.TryGetValue(icao, out string? name) ? name : null;
}
