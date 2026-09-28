import type {Metadata} from 'next';
import {CatalogAdministration} from '../../../modules/catalog/CatalogAdministration';
export const metadata:Metadata={title:'Kelola katalog | Unit Toko',description:'Pengelolaan produk dan kemasan dalam cakupan yang disetujui.',robots:{index:false,follow:false}};
export default function CatalogPage(){return <CatalogAdministration/>;}
